using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Exchange.Abstractions;
using TradingBot.Risk.Abstractions;

namespace TradingBot.Risk.KillSwitch;

/// <summary>
/// Process-wide implementation of <see cref="IKillSwitch"/>. State is held
/// in three places:
///   1. In-memory volatile fields  — read by <see cref="IsTripped"/> on the hot path.
///   2. Redis hash <c>tradingbot:killswitch</c> — replicated across processes
///      (background workers, cli tools); presence of <c>tripped=1</c> trips
///      every replica on next <see cref="RefreshFromCache"/>.
///   3. <c>dbo.RiskEvents</c> with EventType <see cref="EventTypeTrip"/> /
///      <see cref="EventTypeReset"/> — durable audit trail.
///
/// When Redis is not configured (in-memory cache only), 1) and 3) still work;
/// the multi-process replication is degraded. Tests use that mode.
///
/// Critical alerting via <see cref="IAlertSink"/> if registered (S11). When
/// the alert sink isn't wired in (S7-only build), the trip still goes through
/// — alerting is a downstream observer, not a gate on trip success.
/// </summary>
public sealed class KillSwitch : IKillSwitch
{
    public const string EventTypeTrip   = "KILL_SWITCH_TRIPPED";
    public const string EventTypeReset  = "KILL_SWITCH_RESET";

    // Redis key shape: hash with fields {tripped, source, reason, trippedAtUtc}.
    private const string RedisKey = "tradingbot:killswitch";
    private const string FieldTripped     = "tripped";
    private const string FieldSource      = "source";
    private const string FieldReason      = "reason";
    private const string FieldTrippedAt   = "trippedAtUtc";

    private readonly IConnectionMultiplexer? _redis;
    private readonly IServiceScopeFactory _scopes;
    private readonly IBinanceKillSwitch _binanceKillSwitch;
    private readonly IAlertSink? _alerts;
    private readonly IClock _clock;
    private readonly ILogger<KillSwitch> _log;

    private readonly object _gate = new();
    private bool _tripped;
    private string? _reason;
    private DateTime? _trippedAtUtc;
    private KillSwitchSource _source = KillSwitchSource.None;

    // KillSwitch is a singleton; IRiskEventRepository is scoped (DB-backed).
    // We acquire a fresh scope per Trip/Reset to avoid capturing a scoped
    // service inside a singleton (DI scope-validation in Development).
    public KillSwitch(
        IServiceScopeFactory scopes,
        IBinanceKillSwitch binanceKillSwitch,
        IClock clock,
        ILogger<KillSwitch> log,
        IConnectionMultiplexer? redis = null,
        IAlertSink? alerts = null)
    {
        _scopes = scopes;
        _binanceKillSwitch = binanceKillSwitch;
        _clock = clock;
        _log = log;
        _redis = redis;
        _alerts = alerts;

        // Pick up any pre-existing state on construction (e.g. another
        // instance tripped before this one came up).
        RefreshFromCache();
    }

    public bool             IsTripped     { get { lock (_gate) return _tripped || _binanceKillSwitch.IsTripped; } }
    public string?          Reason        { get { lock (_gate) return _reason ?? _binanceKillSwitch.Reason; } }
    public DateTime?        TrippedAtUtc  { get { lock (_gate) return _trippedAtUtc ?? _binanceKillSwitch.TrippedAtUtc; } }
    public KillSwitchSource Source        { get { lock (_gate) return _source != KillSwitchSource.None ? _source
                                                                : _binanceKillSwitch.IsTripped ? KillSwitchSource.Http418Ban : KillSwitchSource.None; } }

    public async Task TripAsync(KillSwitchSource source, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        bool firstTrip;
        DateTime trippedAtUtc;
        lock (_gate)
        {
            firstTrip = !_tripped;
            _tripped = true;
            _trippedAtUtc ??= _clock.UtcNow;
            _reason = reason;
            _source = source;
            trippedAtUtc = _trippedAtUtc.Value;
        }

        // Mirror to the Binance-only flag so the Polly pipeline sees it too.
        _binanceKillSwitch.Trip(reason, retryAfterUtc: null);

        if (firstTrip)
        {
            _log.LogCritical(
                "KILL SWITCH TRIPPED source={Source} reason={Reason} at={At:o}",
                source, reason, trippedAtUtc);
        }
        else
        {
            _log.LogWarning(
                "Kill switch re-tripped (already active) source={Source} reason={Reason}",
                source, reason);
        }

        // Write to Redis (best-effort).
        await SyncTripToRedisAsync(source, reason, trippedAtUtc).ConfigureAwait(false);

        // Audit row. Failures are tolerated — the in-memory + Redis state is
        // already consistent and is what the gate relies on.
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var riskEvents = scope.ServiceProvider.GetRequiredService<IRiskEventRepository>();
            await riskEvents.InsertAsync(new RiskEvent
            {
                EventTime = trippedAtUtc,
                EventType = EventTypeTrip,
                Severity  = "CRITICAL",
                Payload   = $"source={source} reason={reason}",
                Acted     = true,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Failed to persist KILL_SWITCH_TRIPPED RiskEvent");
        }

        if (firstTrip && _alerts is not null)
        {
            try
            {
                await _alerts.SendAsync(
                    AlertSeverity.Critical,
                    title:   "KILL SWITCH TRIPPED",
                    body:    $"source={source}\nreason={reason}\nat={trippedAtUtc:o}",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Failed to dispatch CRITICAL kill-switch alert");
            }
        }
    }

    public async Task ResetAsync(string operatorNote, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorNote);

        bool wasTripped;
        lock (_gate)
        {
            wasTripped = _tripped;
            _tripped = false;
            _reason = null;
            _trippedAtUtc = null;
            _source = KillSwitchSource.None;
        }

        _binanceKillSwitch.Reset();

        try
        {
            if (_redis is not null)
            {
                var db = _redis.GetDatabase();
                await db.KeyDeleteAsync(RedisKey).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Failed to clear kill-switch state in Redis");
        }

        if (!wasTripped)
        {
            _log.LogInformation("Kill switch reset (was already clear). note={Note}", operatorNote);
            return;
        }

        _log.LogWarning("Kill switch RESET by operator. note={Note}", operatorNote);

        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var riskEvents = scope.ServiceProvider.GetRequiredService<IRiskEventRepository>();
            await riskEvents.InsertAsync(new RiskEvent
            {
                EventTime = _clock.UtcNow,
                EventType = EventTypeReset,
                Severity  = "WARN",
                Payload   = operatorNote,
                Acted     = true,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Failed to persist KILL_SWITCH_RESET RiskEvent");
        }
    }

    public void RefreshFromCache()
    {
        if (_redis is null) return;

        try
        {
            var db = _redis.GetDatabase();
            var entries = db.HashGetAll(RedisKey);
            if (entries.Length == 0) return;

            string? trippedRaw = null, sourceRaw = null, reasonRaw = null, atRaw = null;
            foreach (var e in entries)
            {
                var name = (string?)e.Name ?? string.Empty;
                if (name == FieldTripped)   trippedRaw = e.Value;
                else if (name == FieldSource) sourceRaw = e.Value;
                else if (name == FieldReason) reasonRaw = e.Value;
                else if (name == FieldTrippedAt) atRaw = e.Value;
            }

            var tripped = string.Equals(trippedRaw, "1", StringComparison.Ordinal);
            if (!tripped) return;

            lock (_gate)
            {
                _tripped = true;
                _reason = reasonRaw ?? "redis-replicated";
                if (Enum.TryParse<KillSwitchSource>(sourceRaw, out var src)) _source = src;
                if (DateTime.TryParse(atRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                    _trippedAtUtc = dt;
                else
                    _trippedAtUtc ??= _clock.UtcNow;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Kill switch Redis refresh failed (non-fatal)");
        }
    }

    private async Task SyncTripToRedisAsync(KillSwitchSource source, string reason, DateTime trippedAtUtc)
    {
        if (_redis is null) return;
        try
        {
            var db = _redis.GetDatabase();
            await db.HashSetAsync(RedisKey, new HashEntry[]
            {
                new(FieldTripped,   "1"),
                new(FieldSource,    source.ToString()),
                new(FieldReason,    reason),
                new(FieldTrippedAt, trippedAtUtc.ToString("o", CultureInfo.InvariantCulture)),
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Failed to write kill-switch state to Redis");
        }
    }
}

/// Minimal alert seam exposed for S11. The Risk module owns the interface
/// definition only — concrete sinks (Telegram via n8n, SendGrid email)
/// land in S11. When no implementation is registered, KillSwitch alerts
/// become no-ops.
public interface IAlertSink
{
    Task SendAsync(AlertSeverity severity, string title, string body, CancellationToken cancellationToken);
}

public enum AlertSeverity
{
    Info     = 0,
    Warn     = 1,
    Error    = 2,
    Critical = 3,
}

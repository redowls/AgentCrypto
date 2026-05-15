using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Observability;
using TradingBot.Data.Abstractions;
using TradingBot.Observability.Alerts.Configuration;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Alerts;

/// <summary>
/// Central IAlertSink. Computes a fingerprint, consults the dedup cache,
/// fans out to the registered IAlertTransports per the severity-to-transport
/// route table, then writes a row to AlertJournal listing the transports that
/// actually succeeded. Transport failures are logged and swallowed — alerting
/// must never break the caller.
/// </summary>
public sealed class AlertRouter : IAlertSink
{
    private readonly IReadOnlyList<IAlertTransport> _transports;
    private readonly AlertDedupCache _dedup;
    private readonly IServiceScopeFactory _scopes;
    private readonly IClock _clock;
    private readonly AlertRoutingOptions _opts;
    private readonly ITradingMetrics _metrics;
    private readonly ILogger<AlertRouter> _log;

    public AlertRouter(
        IEnumerable<IAlertTransport> transports,
        AlertDedupCache dedup,
        IServiceScopeFactory scopes,
        IClock clock,
        IOptions<AlertRoutingOptions> opts,
        ITradingMetrics metrics,
        ILogger<AlertRouter> log)
    {
        _transports = transports.ToList();
        _dedup      = dedup;
        _scopes     = scopes;
        _clock      = clock;
        _opts       = opts.Value;
        _metrics    = metrics;
        _log        = log;
    }

    public async Task SendAsync(AlertSeverity severity, string title, string body, CancellationToken cancellationToken)
    {
        var fp  = AlertFingerprint.Compute(severity, title, body);
        var now = _clock.UtcNow;

        if (_dedup.IsDuplicate(fp, now))
        {
            _metrics.IncAlertDeduped(severity.ToString());
            return;
        }

        if (!_opts.Routes.TryGetValue(severity, out var route))
            route = [AlertTransportKind.Log];

        var actual = new List<AlertTransportKind>(route.Length);
        foreach (var kind in route)
        {
            var sink = _transports.FirstOrDefault(t => t.Kind == kind);
            if (sink is null) continue;
            try
            {
                await sink.SendAsync(severity, title, body, cancellationToken).ConfigureAwait(false);
                actual.Add(kind);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Alert transport {Kind} failed for {Title}", kind, title);
            }
        }

        try
        {
            // IAlertJournalRepository is scoped — open a fresh scope per call
            // since AlertRouter itself is a singleton consumed by other singletons
            // (KillSwitch, watchdog).
            await using var scope = _scopes.CreateAsyncScope();
            var journal = scope.ServiceProvider.GetRequiredService<IAlertJournalRepository>();
            await journal.InsertAsync(new AlertJournalRow(
                SentAtUtc:     now,
                Severity:      (byte)severity,
                Title:         title,
                Body:          body,
                Fingerprint:   fp,
                Transports:    string.Join(',', actual),
                InstanceId:    _opts.InstanceId,
                CorrelationId: SignalContext.Current),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Failed to persist AlertJournal row for {Title}", title);
        }
    }
}

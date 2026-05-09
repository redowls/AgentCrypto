# Section 11 — Observability, Alerting, Health (Design Spec)

**Date:** 2026-05-09
**Status:** Draft, pending implementation plan
**Scope:** Covers §9.5 / §9.6 of the design doc (`compass_artifact_*.md`) and the seven deliverables listed in the Section 11 prompt.

---

## 1. Goals & posture

Build the production-shape observability surface — structured logs with correlation IDs and PII redaction, Prometheus metrics, alert routing with dedup and a daily/warn-digest, and lifecycle-correct health checks — but ship every external provider as **feature-flagged stubs** that default OFF. A `dotnet run` against a fresh checkout works without Telegram, SendGrid, or App Insights credentials; live providers are exercised through a documented smoke test, not CI.

### Non-goals

- No Redis-stream alert pipeline. Alerts go directly to provider APIs.
- No `workflow_telegram_alerts.json` or `workflow_daily_digest.json` n8n exports — bot self-delivers these.
- No System.Diagnostics.Metrics / OpenTelemetry adapter. `ITradingMetrics` is the abstraction.
- No metrics polling hosted service — gauges update inline next to existing snapshot writes.
- No `/health/startup` k8s lifecycle phase.

---

## 2. Architecture & module boundary

A new library project owns logging enrichers, alert sinks, metrics, digests, and the new health checks:

```
src/TradingBot.Observability/
├── TradingBot.Observability.csproj          // refs Core, Data, Risk
├── DependencyInjection/
│   └── ObservabilityServiceCollectionExtensions.cs   // AddObservability(...)
├── Logging/
│   ├── SignalContext.cs                     // AsyncLocal<Guid?> push/pop scope
│   ├── CorrelationIdEnricher.cs             // Serilog enricher
│   └── SensitiveDataEnricher.cs             // value redaction
├── Metrics/
│   └── PrometheusTradingMetrics.cs          // ITradingMetrics impl using prometheus-net
├── Alerts/
│   ├── AlertRouter.cs                       // IAlertSink: dedup + fan-out + journal
│   ├── AlertDedupCache.cs                   // ConcurrentDictionary<fingerprint, lastSentUtc>
│   ├── AlertFingerprint.cs                  // sha256(severity|title|body)
│   ├── ITelegramSender.cs / TelegramSender.cs
│   ├── IEmailSender.cs    / SendGridEmailSender.cs
│   ├── Transports/LoggingAlertTransport.cs
│   ├── Transports/TelegramAlertTransport.cs
│   ├── Transports/SendGridAlertTransport.cs
│   ├── Transports/AppInsightsAlertTransport.cs
│   └── Configuration/
│       ├── TelegramOptions.cs               // moved from Worker; +BotTokenSecretName
│       ├── SendGridOptions.cs
│       ├── AppInsightsOptions.cs
│       └── AlertRoutingOptions.cs
├── Digest/
│   ├── WarnDigestJob.cs                     // 6h Quartz job
│   ├── WarnDigestRenderer.cs                // pure
│   ├── DailyDigestJob.cs                    // 06:00 UTC Quartz job
│   ├── DigestRenderer.cs                    // pure HTML
│   └── DigestData.cs
└── HealthChecks/
    ├── ProcessAliveHealthCheck.cs           // tag: "live"
    ├── KillSwitchHealthCheck.cs             // tag: "ready"
    └── BinanceKillSwitchHealthCheck.cs      // tag: "ready"

src/TradingBot.Core/Observability/
├── ITradingMetrics.cs
└── NullTradingMetrics.cs                    // default no-op

src/TradingBot.Data/
├── Abstractions/IAlertJournalRepository.cs
└── Repositories/AlertJournalRepository.cs   // Dapper

sql/migrations/010_alerts.sql

dashboards/grafana/tradingbot.json
n8n/workflow_news_ingest.json
docs/section11-smoke-test.md
```

Project-reference rule unchanged: Observability depends on Core, Data, Risk (it consumes the existing `IAlertSink` interface from `TradingBot.Risk.KillSwitch`). No upstream module gains a new dependency.

---

## 3. Logging

### `SignalContext` — AsyncLocal correlation scope

```csharp
public static class SignalContext
{
    private static readonly AsyncLocal<Guid?> _current = new();
    public static Guid? Current => _current.Value;

    public static IDisposable BeginSignal(Guid signalId)
    {
        var prev = _current.Value;
        _current.Value = signalId;
        return new Scope(prev);
    }

    private sealed class Scope(Guid? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}
```

**Push sites in S11 (only three):**
1. `SignalEngine` — wraps the "new signal published" log line.
2. `SignalApprovalHostedService` — wraps each signal's risk evaluation.
3. `ExecutionEngine.RunAsync` — wraps each `OrderIntent`'s processing iteration.

Channel handoffs lose AsyncLocal context, but `Signal` and `OrderIntent` carry `SignalId` so each consumer re-opens the scope. Future sections may extend coverage (e.g., fill-to-signal lookups in `UserDataReactor`).

### `CorrelationIdEnricher`

Reads `SignalContext.Current`, adds property `CorrelationId` if present. Registered as `ILogEventEnricher` in DI; existing `.ReadFrom.Services(services)` in `Program.cs` activates it. No `appsettings.json` change required.

### `SensitiveDataEnricher`

Mutates property values matching configured key names with `***REDACTED***`. Implemented as `ILogEventEnricher` (Serilog "filters" are include/exclude predicates; we want value rewrite).

```jsonc
"Logging": {
  "Sensitive": {
    "RedactedKeys": ["ApiKey", "ApiSecret", "BotToken", "Authorization", "Password", "SasToken"],
    "MaskOrderQuantities": false
  }
}
```

When `MaskOrderQuantities=true`, `Quantity` and `Qty` are added to the redaction set at composition time.

---

## 4. Metrics

### `ITradingMetrics` — Core-level abstraction

14 instruments:

| Method | Type | Labels |
|---|---|---|
| `IncSignal(strategy, symbol, side)` | Counter | strategy, symbol, side |
| `IncOrder(status, side, symbol)` | Counter | status, side, symbol |
| `IncOrderFilled(side, symbol)` | Counter | side, symbol |
| `IncOrderCanceled(side, symbol)` | Counter | side, symbol |
| `SetPositionPnl(symbol, usd)` | Gauge | symbol |
| `SetAccountEquity(usd)` | Gauge | — |
| `SetDrawdown(pct)` | Gauge | — |
| `IncAiCall(purpose, result)` | Counter | purpose, result |
| `AddAiCost(purpose, usd)` | Counter | purpose |
| `IncWsReconnect(account, stream)` | Counter | account, stream |
| `ObserveStrategyLatency(strategy, ms)` | Histogram | strategy |
| `ObserveOrderFillLatency(side, symbol, ms)` | Histogram | side, symbol |
| `SetWsLastEventSeconds(account, stream, sec)` | Gauge | account, stream |
| `IncAlertDeduped(severity)` | Counter | severity |

11 from §9.5 + 2 added for Grafana dashboard support (`ObserveOrderFillLatency`, `SetWsLastEventSeconds`) + 1 internal counter for alert dedup observability (`IncAlertDeduped`). The §9.5 alignment is preserved; the additions are clearly labeled as dashboard-driven.

Exposed Prometheus names use `tradingbot_` prefix (e.g., `tradingbot_signals_total`) to avoid collision with prom-net's built-in `process_*` and `dotnet_*` series.

### Default registration

- `TradingBot.Core.Observability.NullTradingMetrics` — no-op default.
- Each module's DI extension calls `services.TryAddSingleton<ITradingMetrics, NullTradingMetrics>()` defensively so unit tests resolve without `AddObservability`.
- `AddObservability` calls `services.AddSingleton<ITradingMetrics, PrometheusTradingMetrics>()` — last-registered wins for `GetService<T>()`.

### Call-site touches (1–3 lines each)

| Project | File | Action |
|---|---|---|
| Strategies | `SignalEngine.cs` | `IncSignal` after publish; `Stopwatch` around `IStrategy.Evaluate` → `ObserveStrategyLatency` |
| Execution | `ExecutionEngine.cs` | `IncOrder` per status transition |
| Execution | `UserDataReactor.cs` | `IncOrderFilled` / `IncOrderCanceled` on terminal events; `ObserveOrderFillLatency` on first FILLED |
| Risk | `AccountSnapshotHostedService.cs` | `SetAccountEquity`, `SetDrawdown`, `SetPositionPnl(symbol)` per snapshot |
| AI | `ClaudeClient.cs` | `IncAiCall(purpose, result)` — result ∈ `{ok, error, cache_hit, rate_limited}` |
| AI | `DailyCostMeter.cs` | `AddAiCost(purpose, usd)` alongside existing journal write |
| Exchange | `WebSocketWatchdog.cs` | `IncWsReconnect`; `SetWsLastEventSeconds` per tick |

### `/metrics` endpoint

`prometheus-net.AspNetCore` package added to `TradingBot.Observability.csproj`. In `Program.cs` after `MapHealthChecks`:

```csharp
app.MapMetrics();   // exposes /metrics
```

No auth (deployment-environment concern).

---

## 5. Alerts

### Two-tier sink model

`IAlertSink` (defined in `TradingBot.Risk.KillSwitch`) is the public contract callers use. Internal `IAlertTransport` represents one delivery channel:

```csharp
internal interface IAlertTransport
{
    AlertTransportKind Kind { get; }   // Log | Telegram | Email | AppInsights
    Task SendAsync(AlertSeverity sev, string title, string body, CancellationToken ct);
}
```

`AlertRouter` is the single registered `IAlertSink`. It:
1. Computes a fingerprint = `sha256(severity|title|body)` hex.
2. Checks `AlertDedupCache` — if same fingerprint within 5 min, increments `IncAlertDeduped` and returns.
3. Iterates the configured route for the severity, calls each registered `IAlertTransport.SendAsync`, recording which succeeded.
4. Writes one row to `dbo.AlertJournal` listing actually-successful transports.

Transport failures are logged but never bubble up — alerts must not break the calling code path.

### Routing rules (data, not code)

```csharp
public Dictionary<AlertSeverity, AlertTransportKind[]> Routes = new()
{
    [Critical] = [Log, Telegram, Email, AppInsights],
    [Error]    = [Log, Telegram, AppInsights],
    [Warn]     = [Log, AppInsights],   // Telegram delivery via 6h WarnDigestJob, not synchronous
    [Info]     = [Log],
};
```

WARN deliberately does NOT include Telegram in the synchronous path; the `WarnDigestJob` (Quartz, 6h) reads recent WARN rows from `AlertJournal` and posts a single rolled-up Telegram message.

### Dedup

`AlertDedupCache` — `ConcurrentDictionary<string, DateTime>`, 5-min window, in-memory only. Pruner runs when count > 10 000. State lost on restart (acceptable per design call).

### Configuration

```jsonc
"Telegram": {
  "Enabled": false,
  "BotTokenSecretName": "Telegram:BotToken",
  "CriticalChatId": "",
  "WarnChatId": "",
  "InfoChatId": "",                             // unused in S11; kept for forward compat
  "RequestTimeoutMs": 10000
},
"SendGrid": {
  "Enabled": false,
  "ApiKeySecretName": "SendGrid:ApiKey",
  "From": "bot@example.com",
  "To": [],
  "RequestTimeoutMs": 10000
},
"AppInsights": {
  "Enabled": false,
  "ConnectionStringSecretName": "AppInsights:ConnectionString"
},
"Alerts": {
  "DedupWindow":         "00:05:00",
  "WarnDigestInterval":  "06:00:00",
  "DailyDigestCronUtc":  "0 0 6 ? * *"
}
```

`Telegram:BotToken` and `SendGrid:ApiKey` are **NOT** config-bindable fields — they're loaded via `ISecretsProvider` at sink construction (same pattern as `Anthropic:ApiKey`). When `Enabled=true`, the relevant secret must resolve non-empty or host startup fails.

### Transports

- **`LoggingAlertTransport`** — always registered. Maps `Critical→LogCritical`, `Error→LogError`, `Warn→LogWarning`, `Info→LogInformation` on a dedicated `ILogger<LoggingAlertTransport>`. No external dependencies.
- **`TelegramAlertTransport`** — registered only when `Telegram:Enabled=true`. Picks `chat_id` by severity (CRITICAL/ERROR → `CriticalChatId`; WARN → `WarnChatId`; INFO → `InfoChatId`) and delegates to `ITelegramSender`.
- **`SendGridAlertTransport`** — registered only when `SendGrid:Enabled=true`. Uses `IEmailSender` with subject `[{severity}] {title}` and HTML body.
- **`AppInsightsAlertTransport`** — registered only when `AppInsights:Enabled=true`. Calls `TelemetryClient.TrackEvent("BotAlert", { severity, title, instance_id })`; `TrackException` for `Critical`. Adds `Microsoft.ApplicationInsights` package; class is the only place that references it.

### Sender abstractions

Two reusable senders for use by both alert transports and digest jobs:

```csharp
public interface ITelegramSender { Task SendAsync(string chatId, string markdownBody, CancellationToken ct); }
public interface IEmailSender    { Task SendAsync(string subject, string htmlBody, IEnumerable<string> to, CancellationToken ct); }
```

Concrete impls (`TelegramSender`, `SendGridEmailSender`) own:
- Named `HttpClient` (`"telegram"`, `"sendgrid"`) registered via `IHttpClientFactory`.
- Polly retry: 3 attempts, exponential backoff, on 429/5xx/network.
- Credential fetch via `ISecretsProvider` at construction.

Transports are thin adapters that pick chat-id / recipient list per severity and delegate to the senders.

### Bridging existing `IWebSocketAlertSink`

`LoggingWebSocketAlertSink` is replaced by a `WebSocketAlertSink` that bridges into `IAlertSink` (the router). The `IWebSocketAlertSink` interface stays — its shape is WS-specific — so call sites in `WebSocketWatchdog` are unchanged. The WS criticals now flow through the central pipeline.

### Schema

```sql
-- sql/migrations/010_alerts.sql
CREATE TABLE dbo.AlertJournal (
    Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AlertJournal PRIMARY KEY,
    SentAtUtc       DATETIME2(3) NOT NULL,
    Severity        TINYINT       NOT NULL,    -- 0=Info,1=Warn,2=Error,3=Critical
    Title           NVARCHAR(256) NOT NULL,
    Body            NVARCHAR(MAX) NOT NULL,
    Fingerprint     CHAR(64)      NOT NULL,
    Transports      NVARCHAR(128) NOT NULL,    -- comma-sep, e.g. "Log,Telegram,Email"
    InstanceId      NVARCHAR(64)  NOT NULL,
    CorrelationId   UNIQUEIDENTIFIER NULL
);
CREATE INDEX IX_AlertJournal_SentAtUtc          ON dbo.AlertJournal(SentAtUtc);
CREATE INDEX IX_AlertJournal_Severity_SentAtUtc ON dbo.AlertJournal(Severity, SentAtUtc);
```

### Alert call-sites wired in S11 (CRITICAL events only)

| Site | Severity | Notes |
|---|---|---|
| `KillSwitch.TripAsync` | Critical | Already takes optional `IAlertSink`; wire the registration. |
| `BinanceKillSwitch.Trip` | Critical | Same pattern — accepts optional `IAlertSink`. |
| `WebSocketWatchdog` stale-stream | Critical | Via `IWebSocketAlertSink` → bridge → `IAlertSink`. |
| `WebSocketWatchdog` listenKey expired | Critical | Same. |
| `RiskManager` daily-loss / max-DD halt | Critical | Direct `IAlertSink.SendAsync` next to existing log statement. |
| `ReconciliationService` drift trip | Error | Direct. |
| `DailyCostMeter` daily-cap reached | Warn | Direct. |

Other call-sites (order failures, reconcile mismatches, DB connection loss) stay log-only this section.

---

## 6. Health checks

### Endpoint changes

Old → New:
- `/health` → unchanged (full diagnostic dump)
- `/health/live` → **removed**, replaced by `/health/liveness`
- `/health/ready` → **removed**, replaced by `/health/readiness`

No alias redirects. Historical smoke-test docs (S7, S8) reference the old paths — left untouched as time-stamped artifacts. CLAUDE.md and README updated in the same commit.

### Tag remapping

| Check | Was | Now |
|---|---|---|
| `process` | _new_ | `live` (always Healthy) |
| `binance` | `live, ready` | `ready` |
| `websocket` | `live` | `ready` |
| `sqlserver` | `ready` | `ready` |
| `killswitch` | _new_ | `ready` |
| `binance_killswitch` | _new_ | `ready` |

`KillSwitchHealthCheck` consumes `IKillSwitch` (existing in `TradingBot.Risk.Abstractions`); when tripped, returns Unhealthy with `data: { source, reason, trippedAt }`. `BinanceKillSwitchHealthCheck` mirrors for `IBinanceKillSwitch`. Two separate checks instead of one OR'd aggregate so operators see exactly which switch tripped in the readiness payload.

### Wiring

```csharp
var hcBuilder = builder.Services.AddHealthChecks()
    .AddCheck<ProcessAliveHealthCheck>("process",            tags: ["live"])
    .AddCheck<BinancePingHealthCheck>("binance",             tags: ["ready"])
    .AddCheck<WebSocketHealthCheck>("websocket",             tags: ["ready"])
    .AddCheck<KillSwitchHealthCheck>("killswitch",           tags: ["ready"])
    .AddCheck<BinanceKillSwitchHealthCheck>("binance_killswitch", tags: ["ready"]);

if (!string.IsNullOrWhiteSpace(dbConn))
    hcBuilder.AddSqlServer(dbConn, name: "sqlserver", tags: ["ready"]);

app.MapHealthChecks("/health",            new() { ResponseWriter = WriteHealthResponse });
app.MapHealthChecks("/health/liveness",   new() { Predicate = r => r.Tags.Contains("live"),  ResponseWriter = WriteHealthResponse });
app.MapHealthChecks("/health/readiness",  new() { Predicate = r => r.Tags.Contains("ready"), ResponseWriter = WriteHealthResponse });
```

---

## 7. Digests

### `WarnDigestJob` (6h)

Quartz job, cron `0 0 0/6 ? * *`. Reads WARN rows from `AlertJournal` in last 6h via `IAlertJournalRepository.GetWindowAsync`. Empty → no-op. Otherwise renders a Telegram-Markdown bullet list (truncated at 30 entries with "+N more" footer) and posts to `Telegram:WarnChatId` via `ITelegramSender`. Skipped when `Telegram:Enabled=false`.

### `DailyDigestJob` (06:00 UTC)

Quartz job, cron `0 0 6 ? * *`. Aggregates the previous calendar day from existing repos:

- `ITradeHistoryRepository.GetClosedAsync(dayStart, dayEnd)` — closed trades.
- `IPositionRepository.GetOpenAsync()` — open positions snapshot.
- `IAccountSnapshotRepository.GetNearestAsync(at)` — equity at day start and day end (for delta).
- `IAlertJournalRepository.GetWindowAsync(severity: null, dayStart, dayEnd)` — alert summary by severity + list of CRITICAL/ERROR titles.
- `IDailyAiCostReader.GetTotalForDayAsync(dayStart, dayEnd)` — AI cost (NEW thin reader on the existing AI cost journal table; lives in `TradingBot.AI.Abstractions` so Observability doesn't know that schema).

Renders HTML via `DigestRenderer` (one `<table>` per section, inline styles, no CSS framework). Subject: `TradingBot daily digest — yyyy-MM-dd`. Sends via `IEmailSender` to `SendGrid:To`. Skipped when `SendGrid:Enabled=false` or `SendGrid:To` empty.

Repository methods are added if not already present (verified during implementation):
- `IAlertJournalRepository.GetWindowAsync(severity?, sinceUtc, untilUtc, ct)` — new (Section 4).
- `IAccountSnapshotRepository.GetNearestAsync(atUtc, ct)` — likely new.
- `ITradeHistoryRepository.GetClosedAsync(sinceUtc, untilUtc, ct)`, `IPositionRepository.GetOpenAsync(ct)` — verified in implementation; added if missing.

### Quartz registration

`AddQuartz` is idempotent (existing `JournalQuartzJob` registration confirms this pattern). `AddObservability` calls `services.AddQuartz(q => { q.AddJob<WarnDigestJob>(...); q.AddTrigger(...); q.AddJob<DailyDigestJob>(...); q.AddTrigger(...); })` — additive to the AI module's registration.

---

## 8. Artifacts

### Grafana dashboard JSON — `dashboards/grafana/tradingbot.json`

Schema version 38 (Grafana 10+). Datasource templating variable `${DS_PROMETHEUS}`. Seven panels:

1. Equity curve — time series, `tradingbot_account_equity_usd`.
2. Drawdown % — time series with red threshold at -10%, `tradingbot_drawdown_pct`.
3. Open positions — table, `tradingbot_position_pnl_usd` instant by `{symbol}`.
4. WS health — stat panel, `tradingbot_ws_last_event_seconds`, threshold red >30s.
5. Order fill latency — three series, `histogram_quantile(0.50|0.95|0.99, sum(rate(tradingbot_order_fill_latency_ms_bucket[5m])) by (le))`.
6. AI cost by purpose — pie, `sum by (purpose)(increase(tradingbot_ai_cost_usd_total[24h]))`.
7. Daily P&L — bar, `delta(tradingbot_account_equity_usd[1d])` over 14 days.

### n8n workflow — `n8n/workflow_news_ingest.json`

Schedule trigger (5 min) → CryptoPanic GET → filter new posts via Workflow Static Data last_run → POST NDJSON to `{{$env.TRADINGBOT_NEWS_WEBHOOK_URL}}/newsfeed/push` with `X-Webhook-Secret` header → update last_run.

`workflow_telegram_alerts.json` and `workflow_daily_digest.json` are NOT shipped — bot self-delivers.

### Smoke-test doc — `docs/section11-smoke-test.md`

Recipes:
1. Verify `CorrelationId` enricher via `/admin/test-alert` raise.
2. Verify sensitive-data redaction with structured `ApiKey` property.
3. Verify `/metrics` exposes all 14 metric families.
4. Verify `/health/liveness` Healthy when KillSwitch tripped; `/health/readiness` 503 with detail.
5. Live CRITICAL → Telegram + email (requires `Telegram:Enabled=true` + `SendGrid:Enabled=true`).
6. Three fake WARN → manual `WarnDigestJob` trigger → single rolled-up Telegram message.
7. Manual `DailyDigestJob` trigger → email arrives with all sections populated.
8. Import Grafana dashboard JSON → panels populate within 1 min.

### Test endpoint

Env-gated `POST /admin/test-alert` registered only when `Environment != "Production"`:

```csharp
if (!builder.Environment.IsProduction())
{
    app.MapPost("/admin/test-alert", async (TestAlertRequest req, IAlertSink alerts, CancellationToken ct) =>
    {
        await alerts.SendAsync(req.Severity, req.Title, req.Body ?? "Test alert", ct);
        return Results.Accepted();
    });
}
```

---

## 9. Tests

### Unit (Moq + FluentAssertions)

- `SignalContextTests` — AsyncLocal flows across `await Task.Yield()`; nested Begin restores previous on dispose.
- `CorrelationIdEnricherTests` — no scope (no property), inside scope (property present), nested (inner wins, outer restored).
- `SensitiveDataEnricherTests` — listed key redacted; non-listed untouched; `MaskOrderQuantities=true` redacts `Quantity`.
- `PrometheusTradingMetricsTests` — increment/set/observe → assert via `CollectorRegistry.DefaultRegistry.CollectAndExportAsTextAsync()`.
- `NullTradingMetricsTests` — every method is a no-op.
- `AlertRouterTests` — for each `AlertSeverity`, assert `Mock<IAlertTransport>.Verify` called/not-called per `Routes[sev]`; journal write contains actual successful transports.
- `AlertDedupCacheTests` — same fingerprint within 5min returns true; after `Advance(6.minutes)` returns false; different fingerprint never collides.
- `TelegramSenderHttpTests` — `Moq.HttpMessageHandler`; assert URL, body markdown, `chat_id` selected per severity, retry on 429.
- `SendGridEmailSenderHttpTests` — same shape.
- `KillSwitchHealthCheckTests` — `Mock<IKillSwitch>.IsTripped=true` → Unhealthy; reasons surfaced in `data`.
- `BinanceKillSwitchHealthCheckTests` — same.
- `WarnDigestRendererTests` — single, multi, 31-row truncation.
- `DigestRendererTests` — golden-file snapshot against `tests/.../golden/daily_digest.html`.
- `WarnDigestJobTests` — empty journal → no send; populated → one send.
- `DailyDigestJobTests` — fixture builds 3 closed + 1 open + 5 alerts; assert `IEmailSender.SendAsync` called once with subject + sections.

### Integration (Testcontainers.MsSql)

- `AlertJournalRepositoryTests` — insert + `GetWindowAsync` round-trip.
- `HealthEndpointsIntegrationTests` (WebApplicationFactory) — liveness Healthy regardless of state; readiness Unhealthy with KillSwitch tripped + JSON contains `"name":"killswitch"`; `/health` reports all checks.
- `MetricsEndpointIntegrationTests` (WebApplicationFactory) — text format contains all 14 family names.

---

## 10. Configuration & secrets summary

### New config keys

| Key | Default | Notes |
|---|---|---|
| `Logging:Sensitive:RedactedKeys` | `["ApiKey","ApiSecret","BotToken","Authorization","Password","SasToken"]` | property names to redact |
| `Logging:Sensitive:MaskOrderQuantities` | `false` | adds Quantity/Qty when true |
| `Telegram:Enabled` | `false` | flagged off by default |
| `Telegram:BotTokenSecretName` | `"Telegram:BotToken"` | secret key, NOT the value |
| `Telegram:CriticalChatId` | `""` | required when Enabled=true |
| `Telegram:WarnChatId` | `""` | required when Enabled=true |
| `Telegram:InfoChatId` | `""` | unused in S11 |
| `Telegram:RequestTimeoutMs` | `10000` | |
| `SendGrid:Enabled` | `false` | |
| `SendGrid:ApiKeySecretName` | `"SendGrid:ApiKey"` | |
| `SendGrid:From` | `"bot@example.com"` | |
| `SendGrid:To` | `[]` | |
| `SendGrid:RequestTimeoutMs` | `10000` | |
| `AppInsights:Enabled` | `false` | |
| `AppInsights:ConnectionStringSecretName` | `"AppInsights:ConnectionString"` | |
| `Alerts:DedupWindow` | `"00:05:00"` | |
| `Alerts:WarnDigestInterval` | `"06:00:00"` | |
| `Alerts:DailyDigestCronUtc` | `"0 0 6 ? * *"` | |

### New secrets

- `Telegram:BotToken` (when `Telegram:Enabled=true`)
- `SendGrid:ApiKey` (when `SendGrid:Enabled=true`)
- `AppInsights:ConnectionString` (when `AppInsights:Enabled=true`)

Vault key naming follows existing convention with `--` separator: `Telegram--BotToken`, `SendGrid--ApiKey`, `AppInsights--ConnectionString`.

CLAUDE.md "Production secrets" section is updated with the new vault keys; the existing `Telegram:BotToken` user-secret line is updated to specify it's only required when `Telegram:Enabled=true`.

---

## 11. Rollout / implementation order

1. `TradingBot.Core` — `Observability/ITradingMetrics.cs` + `NullTradingMetrics.cs`.
2. Each module's DI extension — defensive `TryAddSingleton<ITradingMetrics, NullTradingMetrics>()`. Tests still green (no behavior change).
3. Surgical metric call-sites in Strategies/Execution/AI/Exchange/Risk. No-op until `AddObservability` overrides.
4. `sql/migrations/010_alerts.sql` + `IAlertJournalRepository` / impl.
5. New `TradingBot.Observability` project + DI extension + all alert/metric/digest pieces.
6. `Worker/Program.cs` — `AddObservability(...)`, rewire health checks, add `/admin/test-alert` env-gated, `MapMetrics()`.
7. Move `TelegramOptions` from Worker to Observability; remove old health-check tags.
8. CLAUDE.md + README path updates (`/health/live` → `/health/liveness`, etc.).
9. Tests, smoke-test doc, dashboard JSON, n8n workflow JSON.

Each step compiles and passes existing tests independently.

---

## 12. Definition of done

- `dotnet build` clean (zero warnings, `TreatWarningsAsErrors=true`).
- `dotnet test` green for all 18 new test files (15 unit + 3 integration) + the existing suite.
- `curl /metrics` returns all 14 `tradingbot_*` metric families.
- `curl /health/liveness` returns `Healthy` even when DB / Binance unreachable.
- `curl /health/readiness` returns `Unhealthy` when either kill switch is tripped, with reason in JSON.
- Smoke-test doc walked end-to-end with live `Telegram:Enabled=true` + `SendGrid:Enabled=true`: alert arrives in Telegram, digest email arrives at 06:00 UTC the next morning.
- Grafana dashboard imports cleanly and shows data within 1 min.
- Default `dotnet run` works in dev with no Telegram / SendGrid / AppInsights credentials configured.

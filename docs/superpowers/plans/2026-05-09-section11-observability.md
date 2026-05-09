# Section 11 — Observability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the §9.5 / §9.6 observability surface — structured logs with correlation IDs and PII redaction, Prometheus metrics, alert routing with dedup and digests, and lifecycle-correct health checks — with all external providers feature-flagged off by default.

**Architecture:** A new `TradingBot.Observability` library owns enrichers, alert sinks, metrics, digests, and health checks. `ITradingMetrics` lives in `TradingBot.Core` so consuming modules don't take a dep on Observability. Alert call-sites consume the existing `IAlertSink` (already defined in `TradingBot.Risk.KillSwitch`).

**Tech Stack:** .NET 8, Serilog, prometheus-net.AspNetCore, Polly, Quartz, Dapper, Testcontainers.MsSql, xUnit + FluentAssertions + Moq.

**Reference spec:** [docs/superpowers/specs/2026-05-09-section11-observability-design.md](../specs/2026-05-09-section11-observability-design.md)

---

## File structure

**Create**
- `src/TradingBot.Core/Observability/ITradingMetrics.cs`
- `src/TradingBot.Core/Observability/NullTradingMetrics.cs`
- `src/TradingBot.Observability/TradingBot.Observability.csproj`
- `src/TradingBot.Observability/DependencyInjection/ObservabilityServiceCollectionExtensions.cs`
- `src/TradingBot.Observability/Logging/SignalContext.cs`
- `src/TradingBot.Observability/Logging/CorrelationIdEnricher.cs`
- `src/TradingBot.Observability/Logging/SensitiveDataEnricher.cs`
- `src/TradingBot.Observability/Logging/SensitiveLoggingOptions.cs`
- `src/TradingBot.Observability/Metrics/PrometheusTradingMetrics.cs`
- `src/TradingBot.Observability/Alerts/AlertFingerprint.cs`
- `src/TradingBot.Observability/Alerts/AlertDedupCache.cs`
- `src/TradingBot.Observability/Alerts/AlertRouter.cs`
- `src/TradingBot.Observability/Alerts/AlertTransportKind.cs`
- `src/TradingBot.Observability/Alerts/IAlertTransport.cs`
- `src/TradingBot.Observability/Alerts/ITelegramSender.cs`
- `src/TradingBot.Observability/Alerts/TelegramSender.cs`
- `src/TradingBot.Observability/Alerts/IEmailSender.cs`
- `src/TradingBot.Observability/Alerts/SendGridEmailSender.cs`
- `src/TradingBot.Observability/Alerts/Transports/LoggingAlertTransport.cs`
- `src/TradingBot.Observability/Alerts/Transports/TelegramAlertTransport.cs`
- `src/TradingBot.Observability/Alerts/Transports/SendGridAlertTransport.cs`
- `src/TradingBot.Observability/Alerts/Transports/AppInsightsAlertTransport.cs`
- `src/TradingBot.Observability/Alerts/Configuration/TelegramOptions.cs` (moved from Worker)
- `src/TradingBot.Observability/Alerts/Configuration/SendGridOptions.cs`
- `src/TradingBot.Observability/Alerts/Configuration/AppInsightsOptions.cs`
- `src/TradingBot.Observability/Alerts/Configuration/AlertRoutingOptions.cs`
- `src/TradingBot.Observability/Digest/DigestData.cs`
- `src/TradingBot.Observability/Digest/WarnDigestRenderer.cs`
- `src/TradingBot.Observability/Digest/WarnDigestJob.cs`
- `src/TradingBot.Observability/Digest/DigestRenderer.cs`
- `src/TradingBot.Observability/Digest/DailyDigestJob.cs`
- `src/TradingBot.Observability/HealthChecks/ProcessAliveHealthCheck.cs`
- `src/TradingBot.Observability/HealthChecks/KillSwitchHealthCheck.cs`
- `src/TradingBot.Observability/HealthChecks/BinanceKillSwitchHealthCheck.cs`
- `src/TradingBot.Observability/WebSocket/RoutingWebSocketAlertSink.cs` (replaces LoggingWebSocketAlertSink as default)
- `src/TradingBot.Data/Abstractions/IAlertJournalRepository.cs`
- `src/TradingBot.Data/Abstractions/AlertJournalRow.cs`
- `src/TradingBot.Data/Repositories/AlertJournalRepository.cs`
- `src/TradingBot.AI/Abstractions/IDailyAiCostReader.cs`
- `src/TradingBot.AI/Cost/DailyAiCostReader.cs`
- `sql/migrations/010_alerts.sql`
- `dashboards/grafana/tradingbot.json`
- `n8n/workflow_news_ingest.json`
- `docs/section11-smoke-test.md`
- `tests/TradingBot.Tests/Observability/...` (one file per unit-tested class)

**Modify**
- `TradingBot.sln` (add Observability project)
- `src/TradingBot.Strategies/DependencyInjection/StrategiesServiceCollectionExtensions.cs` (TryAdd ITradingMetrics)
- `src/TradingBot.Execution/DependencyInjection/ExecutionServiceCollectionExtensions.cs` (TryAdd ITradingMetrics)
- `src/TradingBot.Risk/DependencyInjection/RiskServiceCollectionExtensions.cs` (TryAdd ITradingMetrics)
- `src/TradingBot.AI/DependencyInjection/AiServiceCollectionExtensions.cs` (TryAdd ITradingMetrics, register IDailyAiCostReader)
- `src/TradingBot.Exchange/DependencyInjection/ExchangeServiceCollectionExtensions.cs` (TryAdd ITradingMetrics)
- `src/TradingBot.Strategies/Engine/SignalEngine.cs` (metric call-sites + SignalContext push)
- `src/TradingBot.Execution/Engine/ExecutionEngine.cs` (metric call-sites + SignalContext push)
- `src/TradingBot.Execution/Engine/SignalApprovalHostedService.cs` (SignalContext push)
- `src/TradingBot.Execution/UserData/UserDataReactor.cs` (metric call-sites)
- `src/TradingBot.Risk/Account/AccountSnapshotHostedService.cs` (metric call-sites)
- `src/TradingBot.Risk/Manager/RiskManager.cs` (alert call-site for daily-loss / max-DD halt — already routes via KillSwitch in most cases; add direct alerts where halt is raised without trip)
- `src/TradingBot.Exchange/Resilience/BinanceKillSwitch.cs` (accept optional IAlertSink, raise CRITICAL on Trip)
- `src/TradingBot.Exchange/WebSocket/WebSocketWatchdog.cs` (metric call-sites; alert path unchanged via IWebSocketAlertSink)
- `src/TradingBot.Exchange/DependencyInjection/ExchangeServiceCollectionExtensions.cs` (replace LoggingWebSocketAlertSink registration with RoutingWebSocketAlertSink)
- `src/TradingBot.AI/Claude/ClaudeClient.cs` (metric call-sites)
- `src/TradingBot.AI/Cost/DailyCostMeter.cs` (metric call-site + accept optional IAlertSink, raise WARN on cap reached)
- `src/TradingBot.Execution/Reconciliation/ReconciliationService.cs` (raise IAlertSink ERROR on drift trip)
- `src/TradingBot.Worker/Program.cs` (rewire health checks, MapMetrics, /admin/test-alert env-gated, AddObservability call)
- `src/TradingBot.Worker/Configuration/TelegramOptions.cs` (delete — moved to Observability)
- `src/TradingBot.Worker/appsettings.json` (new sections)
- `CLAUDE.md` (path renames, secret docs)
- `README.md` (path renames)

---

## Phase 0 — Foundation (ITradingMetrics in Core)

### Task 1: ITradingMetrics interface + NullTradingMetrics in Core

**Files:**
- Create: `src/TradingBot.Core/Observability/ITradingMetrics.cs`
- Create: `src/TradingBot.Core/Observability/NullTradingMetrics.cs`
- Create: `tests/TradingBot.Tests/Observability/NullTradingMetricsTests.cs`

- [ ] **Step 1: Write the interface**

```csharp
// src/TradingBot.Core/Observability/ITradingMetrics.cs
namespace TradingBot.Core.Observability;

public interface ITradingMetrics
{
    void IncSignal(string strategy, string symbol, string side);
    void IncOrder(string status, string side, string symbol);
    void IncOrderFilled(string side, string symbol);
    void IncOrderCanceled(string side, string symbol);
    void SetPositionPnl(string symbol, double usd);
    void SetAccountEquity(double usd);
    void SetDrawdown(double pct);
    void IncAiCall(string purpose, string result);
    void AddAiCost(string purpose, double usd);
    void IncWsReconnect(string account, string stream);
    void ObserveStrategyLatency(string strategy, double milliseconds);
    void ObserveOrderFillLatency(string side, string symbol, double milliseconds);
    void SetWsLastEventSeconds(string account, string stream, double secondsSinceLastEvent);
    void IncAlertDeduped(string severity);
}
```

- [ ] **Step 2: Write NullTradingMetrics**

```csharp
// src/TradingBot.Core/Observability/NullTradingMetrics.cs
namespace TradingBot.Core.Observability;

public sealed class NullTradingMetrics : ITradingMetrics
{
    public void IncSignal(string strategy, string symbol, string side) { }
    public void IncOrder(string status, string side, string symbol) { }
    public void IncOrderFilled(string side, string symbol) { }
    public void IncOrderCanceled(string side, string symbol) { }
    public void SetPositionPnl(string symbol, double usd) { }
    public void SetAccountEquity(double usd) { }
    public void SetDrawdown(double pct) { }
    public void IncAiCall(string purpose, string result) { }
    public void AddAiCost(string purpose, double usd) { }
    public void IncWsReconnect(string account, string stream) { }
    public void ObserveStrategyLatency(string strategy, double milliseconds) { }
    public void ObserveOrderFillLatency(string side, string symbol, double milliseconds) { }
    public void SetWsLastEventSeconds(string account, string stream, double secondsSinceLastEvent) { }
    public void IncAlertDeduped(string severity) { }
}
```

- [ ] **Step 3: Write the test**

```csharp
// tests/TradingBot.Tests/Observability/NullTradingMetricsTests.cs
using FluentAssertions;
using TradingBot.Core.Observability;
using Xunit;

namespace TradingBot.Tests.Observability;

public class NullTradingMetricsTests
{
    [Fact]
    public void All_methods_are_no_ops()
    {
        ITradingMetrics m = new NullTradingMetrics();

        // Each call must complete without throwing.
        var act = () =>
        {
            m.IncSignal("s","BTCUSDT","LONG");
            m.IncOrder("FILLED","BUY","BTCUSDT");
            m.IncOrderFilled("BUY","BTCUSDT");
            m.IncOrderCanceled("SELL","ETHUSDT");
            m.SetPositionPnl("BTCUSDT", 12.34);
            m.SetAccountEquity(10_000);
            m.SetDrawdown(-0.05);
            m.IncAiCall("setup","ok");
            m.AddAiCost("regime", 0.001);
            m.IncWsReconnect("Spot","kline");
            m.ObserveStrategyLatency("breakout", 12.3);
            m.ObserveOrderFillLatency("BUY","BTCUSDT", 250);
            m.SetWsLastEventSeconds("Spot","userData", 0.5);
            m.IncAlertDeduped("Warn");
        };
        act.Should().NotThrow();
    }
}
```

- [ ] **Step 4: Run test**

Run: `dotnet test --filter "FullyQualifiedName~NullTradingMetricsTests"`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```bash
git add src/TradingBot.Core/Observability tests/TradingBot.Tests/Observability/NullTradingMetricsTests.cs
git commit -m "feat(core): add ITradingMetrics + NullTradingMetrics for §11"
```

---

### Task 2: Defensive `TryAddSingleton<ITradingMetrics>` in module DI extensions

**Files:**
- Modify: `src/TradingBot.Strategies/DependencyInjection/StrategiesServiceCollectionExtensions.cs`
- Modify: `src/TradingBot.Execution/DependencyInjection/ExecutionServiceCollectionExtensions.cs`
- Modify: `src/TradingBot.Risk/DependencyInjection/RiskServiceCollectionExtensions.cs`
- Modify: `src/TradingBot.AI/DependencyInjection/AiServiceCollectionExtensions.cs`
- Modify: `src/TradingBot.Exchange/DependencyInjection/ExchangeServiceCollectionExtensions.cs`

- [ ] **Step 1: Add the line to each module DI extension**

In each of the five extensions above, add inside `Add<Module>(...)` near the top:

```csharp
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradingBot.Core.Observability;
// ...
services.TryAddSingleton<ITradingMetrics, NullTradingMetrics>();
```

(The `using` directive is added once per file. The line itself is idempotent across multiple `Add<Module>` calls — `TryAdd` is the explicit guarantee.)

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: zero warnings, zero errors.

- [ ] **Step 3: Run all tests**

Run: `dotnet test`
Expected: all green (no behavioural change).

- [ ] **Step 4: Commit**

```bash
git add src/TradingBot.Strategies/DependencyInjection src/TradingBot.Execution/DependencyInjection src/TradingBot.Risk/DependencyInjection src/TradingBot.AI/DependencyInjection src/TradingBot.Exchange/DependencyInjection
git commit -m "chore(di): register NullTradingMetrics defensively in each module"
```

---

## Phase 1 — Database

### Task 3: Migration `010_alerts.sql`

**Files:**
- Create: `sql/migrations/010_alerts.sql`

- [ ] **Step 1: Write the migration**

```sql
-- sql/migrations/010_alerts.sql
-- §11 alert journal. One row per non-deduplicated alert; consumed by the
-- WARN 6h digest and the daily 06:00 UTC digest. Schema is small but indexed
-- on the two access paths (severity+window, plain window).

IF OBJECT_ID('dbo.AlertJournal', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AlertJournal (
        Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AlertJournal PRIMARY KEY,
        SentAtUtc       DATETIME2(3)  NOT NULL,
        Severity        TINYINT       NOT NULL,    -- 0=Info,1=Warn,2=Error,3=Critical
        Title           NVARCHAR(256) NOT NULL,
        Body            NVARCHAR(MAX) NOT NULL,
        Fingerprint     CHAR(64)      NOT NULL,
        Transports      NVARCHAR(128) NOT NULL,
        InstanceId      NVARCHAR(64)  NOT NULL,
        CorrelationId   UNIQUEIDENTIFIER NULL
    );

    CREATE INDEX IX_AlertJournal_SentAtUtc
        ON dbo.AlertJournal (SentAtUtc);

    CREATE INDEX IX_AlertJournal_Severity_SentAtUtc
        ON dbo.AlertJournal (Severity, SentAtUtc);
END
```

- [ ] **Step 2: Apply migration to local dev DB**

Run: `pwsh ./Make-DevDb.ps1`
Expected: DbUp output ends with `Successfully applied 010_alerts.sql`. `dbo.AlertJournal` exists.

- [ ] **Step 3: Commit**

```bash
git add sql/migrations/010_alerts.sql
git commit -m "feat(db): add 010_alerts.sql for AlertJournal (§11)"
```

---

### Task 4: `IAlertJournalRepository` + Dapper impl + integration test

**Files:**
- Create: `src/TradingBot.Data/Abstractions/AlertJournalRow.cs`
- Create: `src/TradingBot.Data/Abstractions/IAlertJournalRepository.cs`
- Create: `src/TradingBot.Data/Repositories/AlertJournalRepository.cs`
- Modify: `src/TradingBot.Data/DependencyInjection/DataServiceCollectionExtensions.cs`
- Create: `tests/TradingBot.Tests/Database/AlertJournalRepositoryTests.cs`

- [ ] **Step 1: Define the row record + interface**

```csharp
// src/TradingBot.Data/Abstractions/AlertJournalRow.cs
namespace TradingBot.Data.Abstractions;

public sealed record AlertJournalRow(
    DateTime SentAtUtc,
    byte     Severity,
    string   Title,
    string   Body,
    string   Fingerprint,
    string   Transports,
    string   InstanceId,
    Guid?    CorrelationId,
    long     Id = 0);
```

```csharp
// src/TradingBot.Data/Abstractions/IAlertJournalRepository.cs
namespace TradingBot.Data.Abstractions;

public interface IAlertJournalRepository
{
    Task InsertAsync(AlertJournalRow row, CancellationToken ct);

    /// <param name="severity">null = all severities</param>
    Task<IReadOnlyList<AlertJournalRow>> GetWindowAsync(
        byte? severity, DateTime sinceUtc, DateTime untilUtc, CancellationToken ct);
}
```

- [ ] **Step 2: Write failing integration test**

```csharp
// tests/TradingBot.Tests/Database/AlertJournalRepositoryTests.cs
using FluentAssertions;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Repositories;
using Xunit;

namespace TradingBot.Tests.Database;

[Collection("SqlServer")]
public class AlertJournalRepositoryTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Insert_then_GetWindow_returns_inserted_row()
    {
        var repo = new AlertJournalRepository(fixture.ConnectionFactory);
        var now  = DateTime.UtcNow;
        var row  = new AlertJournalRow(
            SentAtUtc: now, Severity: 3, Title: "kill switch tripped",
            Body: "daily loss limit", Fingerprint: new string('a', 64),
            Transports: "Log,Telegram", InstanceId: "bot-test", CorrelationId: null);

        await repo.InsertAsync(row, default);
        var rows = await repo.GetWindowAsync(severity: 3, now.AddMinutes(-1), now.AddMinutes(1), default);

        rows.Should().ContainSingle(r => r.Title == "kill switch tripped" && r.Severity == 3);
    }

    [Fact]
    public async Task GetWindow_with_null_severity_returns_all_severities()
    {
        var repo = new AlertJournalRepository(fixture.ConnectionFactory);
        var now  = DateTime.UtcNow;
        await repo.InsertAsync(new AlertJournalRow(now, 1, "warn-a", "x", new string('b', 64), "Log", "bot", null), default);
        await repo.InsertAsync(new AlertJournalRow(now, 2, "err-a",  "x", new string('c', 64), "Log", "bot", null), default);

        var rows = await repo.GetWindowAsync(severity: null, now.AddSeconds(-30), now.AddSeconds(30), default);

        rows.Should().HaveCountGreaterOrEqualTo(2);
        rows.Should().Contain(r => r.Severity == 1);
        rows.Should().Contain(r => r.Severity == 2);
    }
}
```

- [ ] **Step 3: Run test to verify it fails (no impl yet)**

Run: `dotnet test --filter "FullyQualifiedName~AlertJournalRepositoryTests"`
Expected: COMPILE FAIL — `AlertJournalRepository` not found.

- [ ] **Step 4: Implement repository (mirror existing OrderRepository patterns)**

```csharp
// src/TradingBot.Data/Repositories/AlertJournalRepository.cs
using Dapper;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.Data.Repositories;

public sealed class AlertJournalRepository(ISqlConnectionFactory cf) : IAlertJournalRepository
{
    public async Task InsertAsync(AlertJournalRow row, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.AlertJournal (SentAtUtc, Severity, Title, Body, Fingerprint, Transports, InstanceId, CorrelationId)
VALUES (@SentAtUtc, @Severity, @Title, @Body, @Fingerprint, @Transports, @InstanceId, @CorrelationId);";

        using var conn = cf.Create();
        await conn.ExecuteAsync(new CommandDefinition(sql, row, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AlertJournalRow>> GetWindowAsync(
        byte? severity, DateTime sinceUtc, DateTime untilUtc, CancellationToken ct)
    {
        const string sql = @"
SELECT Id, SentAtUtc, Severity, Title, Body, Fingerprint, Transports, InstanceId, CorrelationId
FROM   dbo.AlertJournal
WHERE  SentAtUtc >= @sinceUtc AND SentAtUtc < @untilUtc
       AND (@severity IS NULL OR Severity = @severity)
ORDER  BY SentAtUtc;";

        using var conn = cf.Create();
        var rows = await conn.QueryAsync<AlertJournalRow>(
            new CommandDefinition(sql, new { sinceUtc, untilUtc, severity }, cancellationToken: ct));
        return rows.ToList();
    }
}
```

- [ ] **Step 5: Register in `DataServiceCollectionExtensions.AddTradingData`**

In `src/TradingBot.Data/DependencyInjection/DataServiceCollectionExtensions.cs`, add inside `AddTradingData`:

```csharp
services.AddScoped<IAlertJournalRepository, AlertJournalRepository>();
```

- [ ] **Step 6: Run test to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~AlertJournalRepositoryTests"`
Expected: PASS (2 tests).

- [ ] **Step 7: Commit**

```bash
git add src/TradingBot.Data tests/TradingBot.Tests/Database/AlertJournalRepositoryTests.cs
git commit -m "feat(data): add IAlertJournalRepository + Dapper impl"
```

---

## Phase 2 — Observability project scaffold

### Task 5: Create `TradingBot.Observability` project + add to solution + minimal DI extension

**Files:**
- Create: `src/TradingBot.Observability/TradingBot.Observability.csproj`
- Create: `src/TradingBot.Observability/DependencyInjection/ObservabilityServiceCollectionExtensions.cs`
- Modify: `TradingBot.sln`

- [ ] **Step 1: Write csproj**

```xml
<!-- src/TradingBot.Observability/TradingBot.Observability.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Configuration.Abstractions" Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="8.0.2" />
    <PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks" Version="8.0.10" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="8.0.1" />
    <PackageReference Include="Microsoft.Extensions.Http" Version="8.0.1" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.2" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="8.0.2" />
    <PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.Options.DataAnnotations" Version="8.0.0" />

    <PackageReference Include="Serilog" Version="4.0.2" />
    <PackageReference Include="prometheus-net.AspNetCore" Version="8.2.1" />
    <PackageReference Include="Microsoft.ApplicationInsights" Version="2.22.0" />

    <PackageReference Include="Polly" Version="8.4.2" />

    <PackageReference Include="Quartz" Version="3.13.1" />
    <PackageReference Include="Quartz.Extensions.DependencyInjection" Version="3.13.1" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\TradingBot.Core\TradingBot.Core.csproj" />
    <ProjectReference Include="..\TradingBot.Data\TradingBot.Data.csproj" />
    <ProjectReference Include="..\TradingBot.Risk\TradingBot.Risk.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="TradingBot.Tests" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Stub DI extension**

```csharp
// src/TradingBot.Observability/DependencyInjection/ObservabilityServiceCollectionExtensions.cs
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingBot.Core.Abstractions;

namespace TradingBot.Observability.DependencyInjection;

public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>Wires logging enrichers, metrics, alert routing, digests, and health checks.</summary>
    public static IServiceCollection AddObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        ISecretsProvider bootstrapSecrets)
    {
        // Filled in across Tasks 6–24.
        return services;
    }
}
```

- [ ] **Step 3: Add the project to the solution**

Run: `dotnet sln TradingBot.sln add src/TradingBot.Observability/TradingBot.Observability.csproj`
Expected: `Project added to the solution.`

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: zero warnings, zero errors.

- [ ] **Step 5: Commit**

```bash
git add TradingBot.sln src/TradingBot.Observability/
git commit -m "feat(observability): scaffold TradingBot.Observability project"
```

---

## Phase 3 — Logging primitives

### Task 6: `SignalContext` AsyncLocal scope

**Files:**
- Create: `src/TradingBot.Observability/Logging/SignalContext.cs`
- Create: `tests/TradingBot.Tests/Observability/SignalContextTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/TradingBot.Tests/Observability/SignalContextTests.cs
using FluentAssertions;
using TradingBot.Observability.Logging;
using Xunit;

namespace TradingBot.Tests.Observability;

public class SignalContextTests
{
    [Fact]
    public void Current_is_null_outside_scope()
    {
        SignalContext.Current.Should().BeNull();
    }

    [Fact]
    public void Begin_pushes_id_and_dispose_pops()
    {
        var id = Guid.NewGuid();
        using (SignalContext.BeginSignal(id))
        {
            SignalContext.Current.Should().Be(id);
        }
        SignalContext.Current.Should().BeNull();
    }

    [Fact]
    public void Nested_begin_restores_outer_on_inner_dispose()
    {
        var outer = Guid.NewGuid();
        var inner = Guid.NewGuid();
        using (SignalContext.BeginSignal(outer))
        {
            using (SignalContext.BeginSignal(inner))
            {
                SignalContext.Current.Should().Be(inner);
            }
            SignalContext.Current.Should().Be(outer);
        }
    }

    [Fact]
    public async Task AsyncLocal_flows_across_await()
    {
        var id = Guid.NewGuid();
        using var _ = SignalContext.BeginSignal(id);
        await Task.Yield();
        SignalContext.Current.Should().Be(id);
    }
}
```

- [ ] **Step 2: Verify test fails**

Run: `dotnet test --filter "FullyQualifiedName~SignalContextTests"`
Expected: COMPILE FAIL — `SignalContext` not defined.

- [ ] **Step 3: Implement `SignalContext`**

```csharp
// src/TradingBot.Observability/Logging/SignalContext.cs
namespace TradingBot.Observability.Logging;

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
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = previous;
        }
    }
}
```

- [ ] **Step 4: Verify test passes**

Run: `dotnet test --filter "FullyQualifiedName~SignalContextTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/TradingBot.Observability/Logging/SignalContext.cs tests/TradingBot.Tests/Observability/SignalContextTests.cs
git commit -m "feat(observability): add SignalContext AsyncLocal scope"
```

---

### Task 7: `CorrelationIdEnricher`

**Files:**
- Create: `src/TradingBot.Observability/Logging/CorrelationIdEnricher.cs`
- Create: `tests/TradingBot.Tests/Observability/CorrelationIdEnricherTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/TradingBot.Tests/Observability/CorrelationIdEnricherTests.cs
using FluentAssertions;
using Serilog.Events;
using Serilog.Parsing;
using TradingBot.Observability.Logging;
using Xunit;

namespace TradingBot.Tests.Observability;

public class CorrelationIdEnricherTests
{
    private static LogEvent MakeEvent() => new(
        DateTimeOffset.UtcNow, LogEventLevel.Information, exception: null,
        new MessageTemplate(Enumerable.Empty<MessageTemplateToken>()),
        Enumerable.Empty<LogEventProperty>());

    private static StubFactory Factory() => new();

    [Fact]
    public void Outside_scope_no_property_is_added()
    {
        var enricher = new CorrelationIdEnricher();
        var ev = MakeEvent();
        enricher.Enrich(ev, Factory());
        ev.Properties.Should().NotContainKey("CorrelationId");
    }

    [Fact]
    public void Inside_scope_property_is_added_with_id()
    {
        var enricher = new CorrelationIdEnricher();
        var id = Guid.NewGuid();
        var ev = MakeEvent();
        using (SignalContext.BeginSignal(id))
        {
            enricher.Enrich(ev, Factory());
        }
        ev.Properties["CorrelationId"].ToString().Trim('"').Should().Be(id.ToString("D"));
    }

    private sealed class StubFactory : Serilog.Core.ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }
}
```

- [ ] **Step 2: Verify test fails**

Run: `dotnet test --filter "FullyQualifiedName~CorrelationIdEnricherTests"`
Expected: COMPILE FAIL.

- [ ] **Step 3: Implement enricher**

```csharp
// src/TradingBot.Observability/Logging/CorrelationIdEnricher.cs
using Serilog.Core;
using Serilog.Events;

namespace TradingBot.Observability.Logging;

public sealed class CorrelationIdEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        var id = SignalContext.Current;
        if (id is null) return;
        logEvent.AddPropertyIfAbsent(
            factory.CreateProperty("CorrelationId", id.Value.ToString("D")));
    }
}
```

- [ ] **Step 4: Verify test passes**

Run: `dotnet test --filter "FullyQualifiedName~CorrelationIdEnricherTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/TradingBot.Observability/Logging/CorrelationIdEnricher.cs tests/TradingBot.Tests/Observability/CorrelationIdEnricherTests.cs
git commit -m "feat(observability): add CorrelationIdEnricher (Serilog)"
```

---

### Task 8: `SensitiveDataEnricher` + `SensitiveLoggingOptions`

**Files:**
- Create: `src/TradingBot.Observability/Logging/SensitiveLoggingOptions.cs`
- Create: `src/TradingBot.Observability/Logging/SensitiveDataEnricher.cs`
- Create: `tests/TradingBot.Tests/Observability/SensitiveDataEnricherTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/TradingBot.Tests/Observability/SensitiveDataEnricherTests.cs
using FluentAssertions;
using Microsoft.Extensions.Options;
using Serilog.Events;
using Serilog.Parsing;
using TradingBot.Observability.Logging;
using Xunit;

namespace TradingBot.Tests.Observability;

public class SensitiveDataEnricherTests
{
    private static LogEvent MakeEvent(params (string Name, object Value)[] props) =>
        new(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
            new MessageTemplate(Enumerable.Empty<MessageTemplateToken>()),
            props.Select(p => new LogEventProperty(p.Name, new ScalarValue(p.Value))).ToArray());

    private static SensitiveDataEnricher CreateEnricher(SensitiveLoggingOptions opts) =>
        new(Options.Create(opts));

    private sealed class StubFactory : Serilog.Core.ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }

    [Fact]
    public void Listed_key_value_is_redacted()
    {
        var enricher = CreateEnricher(new() { RedactedKeys = ["ApiKey"] });
        var ev = MakeEvent(("ApiKey", "secret-abc"), ("Symbol", "BTCUSDT"));

        enricher.Enrich(ev, new StubFactory());

        ev.Properties["ApiKey"].ToString().Should().Contain("REDACTED");
        ev.Properties["Symbol"].ToString().Should().Contain("BTCUSDT");
    }

    [Fact]
    public void MaskOrderQuantities_true_redacts_Quantity()
    {
        var enricher = CreateEnricher(new() { RedactedKeys = [], MaskOrderQuantities = true });
        var ev = MakeEvent(("Quantity", 0.123));

        enricher.Enrich(ev, new StubFactory());

        ev.Properties["Quantity"].ToString().Should().Contain("REDACTED");
    }

    [Fact]
    public void Match_is_case_insensitive()
    {
        var enricher = CreateEnricher(new() { RedactedKeys = ["ApiKey"] });
        var ev = MakeEvent(("apikey", "v"));

        enricher.Enrich(ev, new StubFactory());

        ev.Properties["apikey"].ToString().Should().Contain("REDACTED");
    }
}
```

- [ ] **Step 2: Verify test fails**

Run: `dotnet test --filter "FullyQualifiedName~SensitiveDataEnricherTests"`
Expected: COMPILE FAIL.

- [ ] **Step 3: Implement options + enricher**

```csharp
// src/TradingBot.Observability/Logging/SensitiveLoggingOptions.cs
namespace TradingBot.Observability.Logging;

public sealed class SensitiveLoggingOptions
{
    public const string SectionName = "Logging:Sensitive";

    public IList<string> RedactedKeys { get; init; } =
        ["ApiKey", "ApiSecret", "BotToken", "Authorization", "Password", "SasToken"];

    public bool MaskOrderQuantities { get; init; } = false;
}
```

```csharp
// src/TradingBot.Observability/Logging/SensitiveDataEnricher.cs
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;

namespace TradingBot.Observability.Logging;

public sealed class SensitiveDataEnricher : ILogEventEnricher
{
    private const string Redaction = "***REDACTED***";
    private readonly HashSet<string> _keys;

    public SensitiveDataEnricher(IOptions<SensitiveLoggingOptions> opts)
    {
        _keys = new HashSet<string>(opts.Value.RedactedKeys, StringComparer.OrdinalIgnoreCase);
        if (opts.Value.MaskOrderQuantities)
        {
            _keys.Add("Quantity");
            _keys.Add("Qty");
        }
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        foreach (var key in logEvent.Properties.Keys.ToArray())
        {
            if (_keys.Contains(key))
                logEvent.AddOrUpdateProperty(factory.CreateProperty(key, Redaction));
        }
    }
}
```

- [ ] **Step 4: Verify test passes**

Run: `dotnet test --filter "FullyQualifiedName~SensitiveDataEnricherTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/TradingBot.Observability/Logging tests/TradingBot.Tests/Observability/SensitiveDataEnricherTests.cs
git commit -m "feat(observability): add SensitiveDataEnricher (Serilog) + options"
```

---

## Phase 4 — Metrics

### Task 9: `PrometheusTradingMetrics`

**Files:**
- Create: `src/TradingBot.Observability/Metrics/PrometheusTradingMetrics.cs`
- Create: `tests/TradingBot.Tests/Observability/PrometheusTradingMetricsTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/TradingBot.Tests/Observability/PrometheusTradingMetricsTests.cs
using FluentAssertions;
using Prometheus;
using TradingBot.Observability.Metrics;
using Xunit;

namespace TradingBot.Tests.Observability;

public class PrometheusTradingMetricsTests
{
    [Fact]
    public async Task IncSignal_emits_signals_total_family()
    {
        var registry = Metrics.NewCustomRegistry();
        var m = new PrometheusTradingMetrics(registry);
        m.IncSignal("breakout", "BTCUSDT", "LONG");

        var text = await CollectAsText(registry);
        text.Should().Contain("tradingbot_signals_total");
        text.Should().MatchRegex(@"strategy=""breakout""[^\n]*symbol=""BTCUSDT""[^\n]*side=""LONG""[^\n]*\}\s+1");
    }

    [Fact]
    public async Task SetAccountEquity_emits_account_equity_gauge()
    {
        var registry = Metrics.NewCustomRegistry();
        var m = new PrometheusTradingMetrics(registry);
        m.SetAccountEquity(12345.67);

        (await CollectAsText(registry))
            .Should().MatchRegex(@"tradingbot_account_equity_usd\s+12345\.67");
    }

    [Fact]
    public async Task ObserveStrategyLatency_emits_histogram_buckets()
    {
        var registry = Metrics.NewCustomRegistry();
        var m = new PrometheusTradingMetrics(registry);
        m.ObserveStrategyLatency("trend", 5);
        m.ObserveStrategyLatency("trend", 50);

        var text = await CollectAsText(registry);
        text.Should().Contain("tradingbot_strategy_latency_ms_bucket");
        text.Should().Contain("tradingbot_strategy_latency_ms_count");
        text.Should().Contain("tradingbot_strategy_latency_ms_sum");
    }

    private static async Task<string> CollectAsText(CollectorRegistry registry)
    {
        await using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
```

- [ ] **Step 2: Verify test fails**

Run: `dotnet test --filter "FullyQualifiedName~PrometheusTradingMetricsTests"`
Expected: COMPILE FAIL.

- [ ] **Step 3: Implement `PrometheusTradingMetrics`**

```csharp
// src/TradingBot.Observability/Metrics/PrometheusTradingMetrics.cs
using Prometheus;
using TradingBot.Core.Observability;

namespace TradingBot.Observability.Metrics;

public sealed class PrometheusTradingMetrics : ITradingMetrics
{
    private readonly Counter   _signals;
    private readonly Counter   _orders;
    private readonly Counter   _ordersFilled;
    private readonly Counter   _ordersCanceled;
    private readonly Gauge     _positionPnl;
    private readonly Gauge     _accountEquity;
    private readonly Gauge     _drawdown;
    private readonly Counter   _aiCalls;
    private readonly Counter   _aiCost;
    private readonly Counter   _wsReconnects;
    private readonly Histogram _strategyLatency;
    private readonly Histogram _orderFillLatency;
    private readonly Gauge     _wsLastEvent;
    private readonly Counter   _alertsDeduped;

    /// <summary>Default ctor uses the global default registry; tests pass a fresh registry.</summary>
    public PrometheusTradingMetrics() : this(Metrics.DefaultRegistry) { }

    public PrometheusTradingMetrics(CollectorRegistry registry)
    {
        var f = Metrics.WithCustomRegistry(registry);

        _signals          = f.CreateCounter("tradingbot_signals_total",          "Signals published.",            new CounterConfiguration   { LabelNames = ["strategy","symbol","side"] });
        _orders           = f.CreateCounter("tradingbot_orders_total",           "Order state transitions.",      new CounterConfiguration   { LabelNames = ["status","side","symbol"] });
        _ordersFilled     = f.CreateCounter("tradingbot_orders_filled_total",    "Orders reaching FILLED.",       new CounterConfiguration   { LabelNames = ["side","symbol"] });
        _ordersCanceled   = f.CreateCounter("tradingbot_orders_canceled_total",  "Orders reaching CANCELED.",     new CounterConfiguration   { LabelNames = ["side","symbol"] });
        _positionPnl      = f.CreateGauge  ("tradingbot_position_pnl_usd",       "Per-symbol unrealized PnL.",    new GaugeConfiguration     { LabelNames = ["symbol"] });
        _accountEquity    = f.CreateGauge  ("tradingbot_account_equity_usd",     "Account equity (USD).");
        _drawdown         = f.CreateGauge  ("tradingbot_drawdown_pct",           "Drawdown vs running peak.");
        _aiCalls          = f.CreateCounter("tradingbot_ai_calls_total",         "Claude API calls.",             new CounterConfiguration   { LabelNames = ["purpose","result"] });
        _aiCost           = f.CreateCounter("tradingbot_ai_cost_usd_total",      "AI USD spent.",                 new CounterConfiguration   { LabelNames = ["purpose"] });
        _wsReconnects     = f.CreateCounter("tradingbot_ws_reconnects_total",    "WebSocket reconnects.",         new CounterConfiguration   { LabelNames = ["account","stream"] });
        _strategyLatency  = f.CreateHistogram("tradingbot_strategy_latency_ms",  "Strategy.Evaluate wall time.",  new HistogramConfiguration { LabelNames = ["strategy"], Buckets = Histogram.ExponentialBuckets(start: 1, factor: 2, count: 12) });
        _orderFillLatency = f.CreateHistogram("tradingbot_order_fill_latency_ms","Submit → first FILLED latency.",new HistogramConfiguration { LabelNames = ["side","symbol"], Buckets = Histogram.ExponentialBuckets(start: 1, factor: 2, count: 17) });
        _wsLastEvent      = f.CreateGauge  ("tradingbot_ws_last_event_seconds",  "Seconds since last WS event.",  new GaugeConfiguration     { LabelNames = ["account","stream"] });
        _alertsDeduped    = f.CreateCounter("tradingbot_alerts_deduped_total",   "Alerts collapsed by dedup.",    new CounterConfiguration   { LabelNames = ["severity"] });
    }

    public void IncSignal(string strategy, string symbol, string side)              => _signals.WithLabels(strategy, symbol, side).Inc();
    public void IncOrder(string status, string side, string symbol)                 => _orders.WithLabels(status, side, symbol).Inc();
    public void IncOrderFilled(string side, string symbol)                          => _ordersFilled.WithLabels(side, symbol).Inc();
    public void IncOrderCanceled(string side, string symbol)                        => _ordersCanceled.WithLabels(side, symbol).Inc();
    public void SetPositionPnl(string symbol, double usd)                           => _positionPnl.WithLabels(symbol).Set(usd);
    public void SetAccountEquity(double usd)                                        => _accountEquity.Set(usd);
    public void SetDrawdown(double pct)                                             => _drawdown.Set(pct);
    public void IncAiCall(string purpose, string result)                            => _aiCalls.WithLabels(purpose, result).Inc();
    public void AddAiCost(string purpose, double usd)                               => _aiCost.WithLabels(purpose).Inc(usd);
    public void IncWsReconnect(string account, string stream)                       => _wsReconnects.WithLabels(account, stream).Inc();
    public void ObserveStrategyLatency(string strategy, double ms)                  => _strategyLatency.WithLabels(strategy).Observe(ms);
    public void ObserveOrderFillLatency(string side, string symbol, double ms)      => _orderFillLatency.WithLabels(side, symbol).Observe(ms);
    public void SetWsLastEventSeconds(string account, string stream, double sec)    => _wsLastEvent.WithLabels(account, stream).Set(sec);
    public void IncAlertDeduped(string severity)                                    => _alertsDeduped.WithLabels(severity).Inc();
}
```

- [ ] **Step 4: Verify test passes**

Run: `dotnet test --filter "FullyQualifiedName~PrometheusTradingMetricsTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/TradingBot.Observability/Metrics tests/TradingBot.Tests/Observability/PrometheusTradingMetricsTests.cs
git commit -m "feat(observability): add PrometheusTradingMetrics with 14 instruments"
```

---

### Task 10: Wire metric call-sites in consuming modules

**Files:**
- Modify: `src/TradingBot.Strategies/Engine/SignalEngine.cs`
- Modify: `src/TradingBot.Execution/Engine/ExecutionEngine.cs`
- Modify: `src/TradingBot.Execution/UserData/UserDataReactor.cs`
- Modify: `src/TradingBot.Risk/Account/AccountSnapshotHostedService.cs`
- Modify: `src/TradingBot.AI/Claude/ClaudeClient.cs`
- Modify: `src/TradingBot.AI/Cost/DailyCostMeter.cs`
- Modify: `src/TradingBot.Exchange/WebSocket/WebSocketWatchdog.cs`

Each touch is small (one ctor injection + one or two method calls). The `NullTradingMetrics` default (Task 2) makes these no-op until `AddObservability` overrides.

> **Important:** `SignalContext` (created in Task 6 under `TradingBot.Observability.Logging`) is relocated to `TradingBot.Core.Observability` as part of this task's Step 2 — modules referenced below pull it from Core after the relocation. Steps 3+ assume Step 2 is done.

- [ ] **Step 1: SignalEngine — inject `ITradingMetrics`, instrument signal publish + strategy eval**

In `SignalEngine.cs`:
1. Add `using TradingBot.Core.Observability;` (covers both `ITradingMetrics` and the relocated `SignalContext`).
2. Add `ITradingMetrics _metrics` field; accept it in the primary constructor.
3. Around each `IStrategy.Evaluate` invocation, wrap with `Stopwatch`:

```csharp
var sw = System.Diagnostics.Stopwatch.StartNew();
var signal = strategy.Evaluate(...);
sw.Stop();
_metrics.ObserveStrategyLatency(strategy.Name, sw.Elapsed.TotalMilliseconds);
```

4. Where the signal is published / logged, also push the SignalContext scope and increment the counter:

```csharp
using var _scope = SignalContext.BeginSignal(signal.SignalId);
_metrics.IncSignal(signal.StrategyCode, signal.Symbol, signal.Side.ToString());
_log.LogInformation("Signal published: {SignalId} {Strategy} {Symbol} {Side}", ...);
```

(Confirm exact field names from existing `SignalEngine.cs` during implementation. The project reference `TradingBot.Strategies` → `TradingBot.Observability` is forbidden by the inward-only rule, so consuming modules only depend on `TradingBot.Core.Observability`. Step 2 below performs the SignalContext relocation that makes this dependency-clean.)

- [ ] **Step 2: Refactor — relocate `SignalContext` to Core**

Before continuing call-site wiring, move `SignalContext.cs` from `src/TradingBot.Observability/Logging/` to `src/TradingBot.Core/Observability/SignalContext.cs`. Change its namespace to `TradingBot.Core.Observability`. Update `CorrelationIdEnricher` and `SignalContextTests` namespaces. Run `dotnet build` and `dotnet test --filter "FullyQualifiedName~SignalContextTests"` — expected PASS.

- [ ] **Step 3: ExecutionEngine — inject `ITradingMetrics`, push SignalContext, instrument order transitions**

In `ExecutionEngine.RunAsync`'s loop body, wrap each iteration that processes an `OrderIntent`:

```csharp
using var _scope = SignalContext.BeginSignal(intent.SignalId);
// existing processing
_metrics.IncOrder(newStatus.ToString(), intent.Side.ToString(), intent.Symbol);
```

- [ ] **Step 4: SignalApprovalHostedService — push SignalContext per dequeued signal**

Where the service reads from the approval channel and calls into `IRiskManager`, wrap:

```csharp
await foreach (var signal in _channel.Reader.ReadAllAsync(ct))
{
    using var _scope = SignalContext.BeginSignal(signal.SignalId);
    // existing processing
}
```

- [ ] **Step 5: UserDataReactor — instrument fill / cancel + fill latency**

When a `FILLED` user-data event arrives:

```csharp
_metrics.IncOrderFilled(order.Side.ToString(), order.Symbol);
var latencyMs = (clock.UtcNow - order.SubmittedAtUtc).TotalMilliseconds;
_metrics.ObserveOrderFillLatency(order.Side.ToString(), order.Symbol, latencyMs);
```

When `CANCELED`:

```csharp
_metrics.IncOrderCanceled(order.Side.ToString(), order.Symbol);
```

(Use the existing field for the "submitted at" timestamp — name varies; verify during implementation.)

- [ ] **Step 6: AccountSnapshotHostedService — set equity / drawdown / per-symbol pnl per snapshot**

Right after the snapshot persist call:

```csharp
_metrics.SetAccountEquity((double)snapshot.EquityUsd);
_metrics.SetDrawdown((double)snapshot.DrawdownPct);
foreach (var pos in openPositions)
    _metrics.SetPositionPnl(pos.Symbol, (double)pos.UnrealizedPnlUsd);
```

- [ ] **Step 7: ClaudeClient — instrument call result**

In each terminal branch of `SendAsync`/`InvokeAsync` (success, error, cache-hit, rate-limited), call:

```csharp
_metrics.IncAiCall(purpose, "ok");      // or "error", "cache_hit", "rate_limited"
```

`purpose` comes from the existing `AiPurpose` enum (`setup`, `regime`, `sentiment`, `journal`).

- [ ] **Step 8: DailyCostMeter — instrument cost increments**

In `Record(decimal spentUsd, string purpose)` (add a `purpose` parameter — see Task 25 for full DailyCostMeter rework). For now, in the existing `Record(decimal)`:

```csharp
_metrics.AddAiCost("unknown", (double)spentUsd);
```

The `purpose` plumbing is fixed in Task 25 when we also add the `IAlertSink` cap-warn path.

- [ ] **Step 9: WebSocketWatchdog — instrument reconnects + last-event freshness**

On each reconnect (where the existing `ReconnectCount` increments):

```csharp
_metrics.IncWsReconnect(health.Account.ToString(), health.StreamId);
```

In the per-tick freshness check (compute seconds since `LastEventUtc`):

```csharp
_metrics.SetWsLastEventSeconds(health.Account.ToString(), health.StreamId,
    (clock.UtcNow - health.LastEventUtc).TotalSeconds);
```

- [ ] **Step 10: Build + run all tests**

Run: `dotnet build`
Expected: zero warnings.

Run: `dotnet test`
Expected: all green (existing tests use `NullTradingMetrics` default — no behavior change).

- [ ] **Step 11: Commit**

```bash
git add src/TradingBot.Strategies src/TradingBot.Execution src/TradingBot.Risk src/TradingBot.AI src/TradingBot.Exchange src/TradingBot.Core/Observability tests/TradingBot.Tests/Observability/SignalContextTests.cs
git commit -m "feat(metrics): wire ITradingMetrics call-sites + relocate SignalContext to Core"
```

---

## Phase 5 — Alert configuration

### Task 11: Configuration option classes

**Files:**
- Create: `src/TradingBot.Observability/Alerts/Configuration/TelegramOptions.cs`
- Create: `src/TradingBot.Observability/Alerts/Configuration/SendGridOptions.cs`
- Create: `src/TradingBot.Observability/Alerts/Configuration/AppInsightsOptions.cs`
- Create: `src/TradingBot.Observability/Alerts/Configuration/AlertRoutingOptions.cs`
- Create: `src/TradingBot.Observability/Alerts/AlertTransportKind.cs`
- Delete (later): `src/TradingBot.Worker/Configuration/TelegramOptions.cs`

- [ ] **Step 1: AlertTransportKind enum**

```csharp
// src/TradingBot.Observability/Alerts/AlertTransportKind.cs
namespace TradingBot.Observability.Alerts;

public enum AlertTransportKind
{
    Log          = 0,
    Telegram     = 1,
    Email        = 2,
    AppInsights  = 3,
}
```

- [ ] **Step 2: TelegramOptions (NEW location)**

```csharp
// src/TradingBot.Observability/Alerts/Configuration/TelegramOptions.cs
using System.ComponentModel.DataAnnotations;

namespace TradingBot.Observability.Alerts.Configuration;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public bool Enabled { get; init; } = false;

    /// Resolved via ISecretsProvider — NOT bound directly from config.
    public string BotTokenSecretName { get; init; } = "Telegram:BotToken";

    [Required] public string CriticalChatId { get; init; } = string.Empty;
    [Required] public string WarnChatId     { get; init; } = string.Empty;
    public string InfoChatId  { get; init; } = string.Empty;

    public int RequestTimeoutMs { get; init; } = 10_000;
}
```

- [ ] **Step 3: SendGridOptions**

```csharp
// src/TradingBot.Observability/Alerts/Configuration/SendGridOptions.cs
namespace TradingBot.Observability.Alerts.Configuration;

public sealed class SendGridOptions
{
    public const string SectionName = "SendGrid";

    public bool Enabled { get; init; } = false;
    public string ApiKeySecretName { get; init; } = "SendGrid:ApiKey";
    public string From { get; init; } = "bot@example.com";
    public IList<string> To { get; init; } = new List<string>();
    public int RequestTimeoutMs { get; init; } = 10_000;
}
```

- [ ] **Step 4: AppInsightsOptions**

```csharp
// src/TradingBot.Observability/Alerts/Configuration/AppInsightsOptions.cs
namespace TradingBot.Observability.Alerts.Configuration;

public sealed class AppInsightsOptions
{
    public const string SectionName = "AppInsights";

    public bool Enabled { get; init; } = false;
    public string ConnectionStringSecretName { get; init; } = "AppInsights:ConnectionString";
}
```

- [ ] **Step 5: AlertRoutingOptions**

```csharp
// src/TradingBot.Observability/Alerts/Configuration/AlertRoutingOptions.cs
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Alerts.Configuration;

public sealed class AlertRoutingOptions
{
    public const string SectionName = "Alerts";

    public string InstanceId { get; init; } = "bot";
    public TimeSpan DedupWindow         { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan WarnDigestInterval  { get; init; } = TimeSpan.FromHours(6);
    public string   DailyDigestCronUtc  { get; init; } = "0 0 6 ? * *";

    public Dictionary<AlertSeverity, AlertTransportKind[]> Routes { get; init; } = new()
    {
        [AlertSeverity.Critical] = [AlertTransportKind.Log, AlertTransportKind.Telegram, AlertTransportKind.Email, AlertTransportKind.AppInsights],
        [AlertSeverity.Error]    = [AlertTransportKind.Log, AlertTransportKind.Telegram, AlertTransportKind.AppInsights],
        [AlertSeverity.Warn]     = [AlertTransportKind.Log, AlertTransportKind.AppInsights],
        [AlertSeverity.Info]     = [AlertTransportKind.Log],
    };
}
```

- [ ] **Step 6: appsettings.json — add new sections**

In `src/TradingBot.Worker/appsettings.json`, replace the existing `"Telegram"` block with:

```jsonc
"Telegram": {
  "Enabled": false,
  "BotTokenSecretName": "Telegram:BotToken",
  "CriticalChatId": "",
  "WarnChatId": "",
  "InfoChatId": "",
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
  "InstanceId": "bot-local-1",
  "DedupWindow":         "00:05:00",
  "WarnDigestInterval":  "06:00:00",
  "DailyDigestCronUtc":  "0 0 6 ? * *"
},
"Logging": {
  "Sensitive": {
    "RedactedKeys": ["ApiKey", "ApiSecret", "BotToken", "Authorization", "Password", "SasToken"],
    "MaskOrderQuantities": false
  }
}
```

- [ ] **Step 7: Delete old TelegramOptions in Worker**

Delete `src/TradingBot.Worker/Configuration/TelegramOptions.cs`. In `Program.cs`, remove the existing `AddOptions<TelegramOptions>().Bind(...)` block (its replacement lives in `AddObservability`, Task 23).

- [ ] **Step 8: Build**

Run: `dotnet build`
Expected: zero warnings, zero errors. The Worker's old `using TradingBot.Worker.Configuration;` for TelegramOptions is removed; if anything else referenced `TelegramOptions` it now needs the new namespace.

- [ ] **Step 9: Commit**

```bash
git add src/TradingBot.Observability/Alerts/Configuration src/TradingBot.Observability/Alerts/AlertTransportKind.cs src/TradingBot.Worker/Program.cs src/TradingBot.Worker/appsettings.json
git rm src/TradingBot.Worker/Configuration/TelegramOptions.cs
git commit -m "feat(observability): move TelegramOptions + add SendGrid/AppInsights/AlertRouting options"
```

---

## Phase 6 — Alert senders (Telegram + SendGrid HTTP clients)

### Task 12: `ITelegramSender` + `TelegramSender`

**Files:**
- Create: `src/TradingBot.Observability/Alerts/ITelegramSender.cs`
- Create: `src/TradingBot.Observability/Alerts/TelegramSender.cs`
- Create: `tests/TradingBot.Tests/Observability/TelegramSenderTests.cs`

- [ ] **Step 1: Define interface**

```csharp
// src/TradingBot.Observability/Alerts/ITelegramSender.cs
namespace TradingBot.Observability.Alerts;

public interface ITelegramSender
{
    Task SendAsync(string chatId, string markdownBody, CancellationToken ct);
}
```

- [ ] **Step 2: Write failing test**

```csharp
// tests/TradingBot.Tests/Observability/TelegramSenderTests.cs
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using TradingBot.Observability.Alerts;
using TradingBot.Observability.Alerts.Configuration;
using Xunit;

namespace TradingBot.Tests.Observability;

public class TelegramSenderTests
{
    [Fact]
    public async Task SendAsync_posts_to_telegram_with_chat_id_and_markdown()
    {
        var captured = new List<HttpRequestMessage>();
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured.Add(CloneRequest(req)))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });

        var http = new HttpClient(handler.Object) { BaseAddress = new Uri("https://api.telegram.org/") };
        var opts = Options.Create(new TelegramOptions { Enabled = true, BotTokenSecretName = "X" });
        var sender = new TelegramSender(http, opts, botToken: "TEST_TOKEN", NullLogger<TelegramSender>.Instance);

        await sender.SendAsync("12345", "*hi*\n\nbody", default);

        captured.Should().ContainSingle();
        captured[0].RequestUri!.AbsoluteUri.Should().Contain("/botTEST_TOKEN/sendMessage");
        var body = await captured[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"chat_id\":\"12345\"");
        body.Should().Contain("\"parse_mode\":\"Markdown\"");
        body.Should().Contain("hi");
    }

    [Fact]
    public async Task SendAsync_retries_on_5xx_then_succeeds()
    {
        var calls = 0;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                calls++;
                return calls < 3
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

        var http = new HttpClient(handler.Object) { BaseAddress = new Uri("https://api.telegram.org/") };
        var opts = Options.Create(new TelegramOptions { Enabled = true });
        var sender = new TelegramSender(http, opts, botToken: "T", NullLogger<TelegramSender>.Instance);

        await sender.SendAsync("1", "x", default);

        calls.Should().Be(3);
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage src)
    {
        var clone = new HttpRequestMessage(src.Method, src.RequestUri)
        {
            Content = src.Content is null ? null : new StringContent(src.Content.ReadAsStringAsync().GetAwaiter().GetResult())
        };
        return clone;
    }
}
```

- [ ] **Step 3: Verify test fails**

Run: `dotnet test --filter "FullyQualifiedName~TelegramSenderTests"`
Expected: COMPILE FAIL.

- [ ] **Step 4: Implement TelegramSender**

```csharp
// src/TradingBot.Observability/Alerts/TelegramSender.cs
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using TradingBot.Observability.Alerts.Configuration;

namespace TradingBot.Observability.Alerts;

public sealed class TelegramSender : ITelegramSender
{
    private readonly HttpClient _http;
    private readonly string _botToken;
    private readonly ResiliencePipeline<HttpResponseMessage> _retry;
    private readonly ILogger<TelegramSender> _log;

    public TelegramSender(
        HttpClient http,
        IOptions<TelegramOptions> opts,
        string botToken,
        ILogger<TelegramSender> log)
    {
        _http = http;
        _botToken = botToken;
        _log = log;
        _http.Timeout = TimeSpan.FromMilliseconds(opts.Value.RequestTimeoutMs);
        _retry = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(200),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .HandleResult(r => (int)r.StatusCode == 429 || (int)r.StatusCode >= 500),
            })
            .Build();
    }

    public async Task SendAsync(string chatId, string markdownBody, CancellationToken ct)
    {
        var url = $"/bot{_botToken}/sendMessage";
        var payload = JsonSerializer.Serialize(new
        {
            chat_id    = chatId,
            text       = markdownBody,
            parse_mode = "Markdown",
            disable_web_page_preview = true,
        });

        using var resp = await _retry.ExecuteAsync(async token =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            return await _http.SendAsync(req, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _log.LogWarning("Telegram send failed status={Status} body={Body}", (int)resp.StatusCode, body);
        }
    }
}
```

- [ ] **Step 5: Verify test passes**

Run: `dotnet test --filter "FullyQualifiedName~TelegramSenderTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/TradingBot.Observability/Alerts/ITelegramSender.cs src/TradingBot.Observability/Alerts/TelegramSender.cs tests/TradingBot.Tests/Observability/TelegramSenderTests.cs
git commit -m "feat(observability): add TelegramSender with Polly retry"
```

---

### Task 13: `IEmailSender` + `SendGridEmailSender`

**Files:**
- Create: `src/TradingBot.Observability/Alerts/IEmailSender.cs`
- Create: `src/TradingBot.Observability/Alerts/SendGridEmailSender.cs`
- Create: `tests/TradingBot.Tests/Observability/SendGridEmailSenderTests.cs`

- [ ] **Step 1: Define interface**

```csharp
// src/TradingBot.Observability/Alerts/IEmailSender.cs
namespace TradingBot.Observability.Alerts;

public interface IEmailSender
{
    Task SendAsync(string subject, string htmlBody, IEnumerable<string> to, CancellationToken ct);
}
```

- [ ] **Step 2: Write failing test**

```csharp
// tests/TradingBot.Tests/Observability/SendGridEmailSenderTests.cs
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using TradingBot.Observability.Alerts;
using TradingBot.Observability.Alerts.Configuration;
using Xunit;

namespace TradingBot.Tests.Observability;

public class SendGridEmailSenderTests
{
    [Fact]
    public async Task SendAsync_posts_v3_mail_send_with_bearer_and_recipients()
    {
        HttpRequestMessage? captured = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Accepted));

        var http = new HttpClient(handler.Object) { BaseAddress = new Uri("https://api.sendgrid.com/") };
        var opts = Options.Create(new SendGridOptions
        {
            Enabled = true, From = "bot@x.io",
            To = new List<string> { "ops@x.io", "alerts@x.io" }
        });
        var sender = new SendGridEmailSender(http, opts, apiKey: "SG.test", NullLogger<SendGridEmailSender>.Instance);

        await sender.SendAsync("Subj", "<p>hi</p>", new[] { "ops@x.io", "alerts@x.io" }, default);

        captured.Should().NotBeNull();
        captured!.RequestUri!.AbsoluteUri.Should().EndWith("/v3/mail/send");
        captured.Headers.Authorization!.ToString().Should().Be("Bearer SG.test");
        var body = await captured.Content!.ReadAsStringAsync();
        body.Should().Contain("\"subject\":\"Subj\"");
        body.Should().Contain("\"from\":{\"email\":\"bot@x.io\"}");
        body.Should().Contain("ops@x.io");
        body.Should().Contain("alerts@x.io");
        body.Should().Contain("text/html");
    }
}
```

- [ ] **Step 3: Verify test fails**

Run: `dotnet test --filter "FullyQualifiedName~SendGridEmailSenderTests"`
Expected: COMPILE FAIL.

- [ ] **Step 4: Implement SendGridEmailSender**

```csharp
// src/TradingBot.Observability/Alerts/SendGridEmailSender.cs
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using TradingBot.Observability.Alerts.Configuration;

namespace TradingBot.Observability.Alerts;

public sealed class SendGridEmailSender : IEmailSender
{
    private readonly HttpClient _http;
    private readonly SendGridOptions _opts;
    private readonly ResiliencePipeline<HttpResponseMessage> _retry;
    private readonly ILogger<SendGridEmailSender> _log;

    public SendGridEmailSender(
        HttpClient http,
        IOptions<SendGridOptions> opts,
        string apiKey,
        ILogger<SendGridEmailSender> log)
    {
        _http = http;
        _opts = opts.Value;
        _log  = log;
        _http.Timeout = TimeSpan.FromMilliseconds(_opts.RequestTimeoutMs);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _retry = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(200),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .HandleResult(r => (int)r.StatusCode == 429 || (int)r.StatusCode >= 500),
            })
            .Build();
    }

    public async Task SendAsync(string subject, string htmlBody, IEnumerable<string> to, CancellationToken ct)
    {
        var recipients = to.Select(addr => new { email = addr }).ToArray();
        if (recipients.Length == 0) return;

        var payload = JsonSerializer.Serialize(new
        {
            personalizations = new[] { new { to = recipients } },
            from    = new { email = _opts.From },
            subject = subject,
            content = new[] { new { type = "text/html", value = htmlBody } },
        });

        using var resp = await _retry.ExecuteAsync(async token =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/v3/mail/send")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            return await _http.SendAsync(req, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _log.LogWarning("SendGrid send failed status={Status} body={Body}", (int)resp.StatusCode, body);
        }
    }
}
```

- [ ] **Step 5: Verify test passes**

Run: `dotnet test --filter "FullyQualifiedName~SendGridEmailSenderTests"`
Expected: PASS (1 test).

- [ ] **Step 6: Commit**

```bash
git add src/TradingBot.Observability/Alerts/IEmailSender.cs src/TradingBot.Observability/Alerts/SendGridEmailSender.cs tests/TradingBot.Tests/Observability/SendGridEmailSenderTests.cs
git commit -m "feat(observability): add SendGridEmailSender with Polly retry"
```

---

## Phase 7 — Alert routing core

### Task 14: `AlertFingerprint` + `AlertDedupCache`

**Files:**
- Create: `src/TradingBot.Observability/Alerts/AlertFingerprint.cs`
- Create: `src/TradingBot.Observability/Alerts/AlertDedupCache.cs`
- Create: `tests/TradingBot.Tests/Observability/AlertDedupCacheTests.cs`

- [ ] **Step 1: Implement `AlertFingerprint`**

```csharp
// src/TradingBot.Observability/Alerts/AlertFingerprint.cs
using System.Security.Cryptography;
using System.Text;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Alerts;

public static class AlertFingerprint
{
    public static string Compute(AlertSeverity severity, string title, string body)
    {
        var input = $"{(int)severity}|{title}|{body}";
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash  = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
```

- [ ] **Step 2: Write failing test for dedup cache**

```csharp
// tests/TradingBot.Tests/Observability/AlertDedupCacheTests.cs
using FluentAssertions;
using Microsoft.Extensions.Options;
using TradingBot.Observability.Alerts;
using TradingBot.Observability.Alerts.Configuration;
using Xunit;

namespace TradingBot.Tests.Observability;

public class AlertDedupCacheTests
{
    private static AlertDedupCache Cache(TimeSpan window) =>
        new(Options.Create(new AlertRoutingOptions { DedupWindow = window }));

    [Fact]
    public void First_call_is_not_duplicate()
    {
        var c = Cache(TimeSpan.FromMinutes(5));
        c.IsDuplicate("fp", DateTime.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void Second_call_within_window_is_duplicate()
    {
        var c = Cache(TimeSpan.FromMinutes(5));
        var t = DateTime.UtcNow;
        c.IsDuplicate("fp", t).Should().BeFalse();
        c.IsDuplicate("fp", t.AddMinutes(2)).Should().BeTrue();
    }

    [Fact]
    public void Second_call_outside_window_is_not_duplicate()
    {
        var c = Cache(TimeSpan.FromMinutes(5));
        var t = DateTime.UtcNow;
        c.IsDuplicate("fp", t).Should().BeFalse();
        c.IsDuplicate("fp", t.AddMinutes(6)).Should().BeFalse();
    }

    [Fact]
    public void Different_fingerprints_never_collide()
    {
        var c = Cache(TimeSpan.FromMinutes(5));
        c.IsDuplicate("a", DateTime.UtcNow).Should().BeFalse();
        c.IsDuplicate("b", DateTime.UtcNow).Should().BeFalse();
    }
}
```

- [ ] **Step 3: Verify test fails**

Run: `dotnet test --filter "FullyQualifiedName~AlertDedupCacheTests"`
Expected: COMPILE FAIL.

- [ ] **Step 4: Implement `AlertDedupCache`**

```csharp
// src/TradingBot.Observability/Alerts/AlertDedupCache.cs
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using TradingBot.Observability.Alerts.Configuration;

namespace TradingBot.Observability.Alerts;

public sealed class AlertDedupCache
{
    private const int PruneThreshold = 10_000;
    private readonly ConcurrentDictionary<string, DateTime> _seen = new();
    private readonly TimeSpan _window;

    public AlertDedupCache(IOptions<AlertRoutingOptions> opts) => _window = opts.Value.DedupWindow;

    /// Returns true when the fingerprint was seen within the dedup window.
    /// Updates the timestamp to nowUtc when accepted (i.e., not duplicate).
    public bool IsDuplicate(string fingerprint, DateTime nowUtc)
    {
        if (_seen.Count > PruneThreshold) Prune(nowUtc);

        var added = false;
        _seen.AddOrUpdate(fingerprint,
            _ => { added = true; return nowUtc; },
            (_, prev) =>
            {
                if (nowUtc - prev < _window) return prev; // duplicate — keep prev
                added = true;
                return nowUtc;                            // window expired — refresh
            });
        return !added;
    }

    private void Prune(DateTime nowUtc)
    {
        foreach (var kv in _seen)
        {
            if (nowUtc - kv.Value >= _window)
                _seen.TryRemove(kv.Key, out _);
        }
    }
}
```

- [ ] **Step 5: Verify tests pass**

Run: `dotnet test --filter "FullyQualifiedName~AlertDedupCacheTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/TradingBot.Observability/Alerts/AlertFingerprint.cs src/TradingBot.Observability/Alerts/AlertDedupCache.cs tests/TradingBot.Tests/Observability/AlertDedupCacheTests.cs
git commit -m "feat(observability): add AlertFingerprint + AlertDedupCache"
```

---

### Task 15: `IAlertTransport` + four transport impls

**Files:**
- Create: `src/TradingBot.Observability/Alerts/IAlertTransport.cs`
- Create: `src/TradingBot.Observability/Alerts/Transports/LoggingAlertTransport.cs`
- Create: `src/TradingBot.Observability/Alerts/Transports/TelegramAlertTransport.cs`
- Create: `src/TradingBot.Observability/Alerts/Transports/SendGridAlertTransport.cs`
- Create: `src/TradingBot.Observability/Alerts/Transports/AppInsightsAlertTransport.cs`

- [ ] **Step 1: Define `IAlertTransport`**

```csharp
// src/TradingBot.Observability/Alerts/IAlertTransport.cs
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Alerts;

public interface IAlertTransport
{
    AlertTransportKind Kind { get; }
    Task SendAsync(AlertSeverity sev, string title, string body, CancellationToken ct);
}
```

- [ ] **Step 2: Implement `LoggingAlertTransport`**

```csharp
// src/TradingBot.Observability/Alerts/Transports/LoggingAlertTransport.cs
using Microsoft.Extensions.Logging;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Alerts.Transports;

public sealed class LoggingAlertTransport(ILogger<LoggingAlertTransport> log) : IAlertTransport
{
    public AlertTransportKind Kind => AlertTransportKind.Log;

    public Task SendAsync(AlertSeverity sev, string title, string body, CancellationToken ct)
    {
        var level = sev switch
        {
            AlertSeverity.Critical => LogLevel.Critical,
            AlertSeverity.Error    => LogLevel.Error,
            AlertSeverity.Warn     => LogLevel.Warning,
            _                      => LogLevel.Information,
        };
        log.Log(level, "ALERT [{Severity}] {Title}: {Body}", sev, title, body);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: Implement `TelegramAlertTransport`**

```csharp
// src/TradingBot.Observability/Alerts/Transports/TelegramAlertTransport.cs
using Microsoft.Extensions.Options;
using TradingBot.Observability.Alerts.Configuration;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Alerts.Transports;

public sealed class TelegramAlertTransport(ITelegramSender sender, IOptions<TelegramOptions> opts) : IAlertTransport
{
    public AlertTransportKind Kind => AlertTransportKind.Telegram;

    public Task SendAsync(AlertSeverity sev, string title, string body, CancellationToken ct)
    {
        var chatId = sev switch
        {
            AlertSeverity.Critical or AlertSeverity.Error => opts.Value.CriticalChatId,
            AlertSeverity.Warn                            => opts.Value.WarnChatId,
            _                                             => opts.Value.InfoChatId,
        };
        if (string.IsNullOrWhiteSpace(chatId)) return Task.CompletedTask;

        var markdown = $"*{Escape(title)}*\n\n{Escape(body)}";
        return sender.SendAsync(chatId, markdown, ct);
    }

    private static string Escape(string s) =>
        s.Replace("_", "\\_").Replace("*", "\\*").Replace("[", "\\[").Replace("`", "\\`");
}
```

- [ ] **Step 4: Implement `SendGridAlertTransport`**

```csharp
// src/TradingBot.Observability/Alerts/Transports/SendGridAlertTransport.cs
using System.Net;
using Microsoft.Extensions.Options;
using TradingBot.Observability.Alerts.Configuration;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Alerts.Transports;

public sealed class SendGridAlertTransport(IEmailSender sender, IOptions<SendGridOptions> opts) : IAlertTransport
{
    public AlertTransportKind Kind => AlertTransportKind.Email;

    public Task SendAsync(AlertSeverity sev, string title, string body, CancellationToken ct)
    {
        if (opts.Value.To.Count == 0) return Task.CompletedTask;

        var subject = $"[{sev}] {title}";
        var html    = $"<p><strong>{WebUtility.HtmlEncode(title)}</strong></p><p>{WebUtility.HtmlEncode(body).Replace("\n", "<br/>")}</p>";
        return sender.SendAsync(subject, html, opts.Value.To, ct);
    }
}
```

- [ ] **Step 5: Implement `AppInsightsAlertTransport`**

```csharp
// src/TradingBot.Observability/Alerts/Transports/AppInsightsAlertTransport.cs
using Microsoft.ApplicationInsights;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Alerts.Transports;

public sealed class AppInsightsAlertTransport(TelemetryClient telemetry) : IAlertTransport
{
    public AlertTransportKind Kind => AlertTransportKind.AppInsights;

    public Task SendAsync(AlertSeverity sev, string title, string body, CancellationToken ct)
    {
        var props = new Dictionary<string, string>
        {
            ["severity"] = sev.ToString(),
            ["title"]    = title,
            ["body"]     = body,
        };
        telemetry.TrackEvent("BotAlert", props);
        if (sev == AlertSeverity.Critical)
            telemetry.TrackException(new ApplicationException($"{title}: {body}"), props);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 6: Build**

Run: `dotnet build`
Expected: zero warnings, zero errors.

- [ ] **Step 7: Commit**

```bash
git add src/TradingBot.Observability/Alerts/IAlertTransport.cs src/TradingBot.Observability/Alerts/Transports
git commit -m "feat(observability): add four IAlertTransport impls"
```

---

### Task 16: `AlertRouter` (the public `IAlertSink`)

**Files:**
- Create: `src/TradingBot.Observability/Alerts/AlertRouter.cs`
- Create: `tests/TradingBot.Tests/Observability/AlertRouterTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/TradingBot.Tests/Observability/AlertRouterTests.cs
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Observability;
using TradingBot.Data.Abstractions;
using TradingBot.Observability.Alerts;
using TradingBot.Observability.Alerts.Configuration;
using TradingBot.Risk.KillSwitch;
using Xunit;

namespace TradingBot.Tests.Observability;

public class AlertRouterTests
{
    private sealed class FixedClock(DateTime t) : IClock { public DateTime UtcNow => t; }

    private static (AlertRouter router, Mock<IAlertJournalRepository> journal,
                    Mock<IAlertTransport> log, Mock<IAlertTransport> tg, Mock<IAlertTransport> em, Mock<IAlertTransport> ai)
        Build(AlertRoutingOptions? opts = null)
    {
        var log = MockTransport(AlertTransportKind.Log);
        var tg  = MockTransport(AlertTransportKind.Telegram);
        var em  = MockTransport(AlertTransportKind.Email);
        var ai  = MockTransport(AlertTransportKind.AppInsights);

        var journal = new Mock<IAlertJournalRepository>();
        var clock = new FixedClock(DateTime.UtcNow);
        var router = new AlertRouter(
            transports: new[] { log.Object, tg.Object, em.Object, ai.Object },
            dedup:    new AlertDedupCache(Options.Create(opts ?? new AlertRoutingOptions())),
            journal:  journal.Object,
            clock:    clock,
            opts:     Options.Create(opts ?? new AlertRoutingOptions()),
            metrics:  new NullTradingMetrics(),
            log:      NullLogger<AlertRouter>.Instance);
        return (router, journal, log, tg, em, ai);
    }

    private static Mock<IAlertTransport> MockTransport(AlertTransportKind kind)
    {
        var m = new Mock<IAlertTransport>();
        m.SetupGet(x => x.Kind).Returns(kind);
        m.Setup(x => x.SendAsync(It.IsAny<AlertSeverity>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return m;
    }

    [Fact]
    public async Task Critical_routes_to_log_telegram_email_appinsights()
    {
        var (r, journal, log, tg, em, ai) = Build();
        await r.SendAsync(AlertSeverity.Critical, "t", "b", default);

        log.Verify(x => x.SendAsync(AlertSeverity.Critical, "t", "b", It.IsAny<CancellationToken>()), Times.Once);
        tg .Verify(x => x.SendAsync(AlertSeverity.Critical, "t", "b", It.IsAny<CancellationToken>()), Times.Once);
        em .Verify(x => x.SendAsync(AlertSeverity.Critical, "t", "b", It.IsAny<CancellationToken>()), Times.Once);
        ai .Verify(x => x.SendAsync(AlertSeverity.Critical, "t", "b", It.IsAny<CancellationToken>()), Times.Once);
        journal.Verify(j => j.InsertAsync(It.IsAny<AlertJournalRow>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Error_routes_to_log_telegram_appinsights_but_not_email()
    {
        var (r, _, log, tg, em, ai) = Build();
        await r.SendAsync(AlertSeverity.Error, "t", "b", default);

        log.Verify(x => x.SendAsync(AlertSeverity.Error, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        tg .Verify(x => x.SendAsync(AlertSeverity.Error, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        ai .Verify(x => x.SendAsync(AlertSeverity.Error, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        em .Verify(x => x.SendAsync(It.IsAny<AlertSeverity>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Warn_routes_to_log_and_appinsights_only()
    {
        var (r, _, log, tg, em, ai) = Build();
        await r.SendAsync(AlertSeverity.Warn, "t", "b", default);

        log.Verify(x => x.SendAsync(AlertSeverity.Warn, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        ai .Verify(x => x.SendAsync(AlertSeverity.Warn, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        tg .Verify(x => x.SendAsync(It.IsAny<AlertSeverity>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        em .Verify(x => x.SendAsync(It.IsAny<AlertSeverity>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Info_routes_to_log_only()
    {
        var (r, _, log, tg, em, ai) = Build();
        await r.SendAsync(AlertSeverity.Info, "t", "b", default);

        log.Verify(x => x.SendAsync(AlertSeverity.Info, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        tg .Verify(x => x.SendAsync(It.IsAny<AlertSeverity>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        em .Verify(x => x.SendAsync(It.IsAny<AlertSeverity>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        ai .Verify(x => x.SendAsync(It.IsAny<AlertSeverity>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Duplicate_within_window_does_not_call_transports_or_journal()
    {
        var (r, journal, log, _, _, _) = Build();
        await r.SendAsync(AlertSeverity.Critical, "t", "b", default);
        await r.SendAsync(AlertSeverity.Critical, "t", "b", default);

        log    .Verify(x => x.SendAsync(It.IsAny<AlertSeverity>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        journal.Verify(j => j.InsertAsync(It.IsAny<AlertJournalRow>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Transport_failure_does_not_block_other_transports_or_journal()
    {
        var (r, journal, log, tg, em, ai) = Build();
        tg.Setup(x => x.SendAsync(It.IsAny<AlertSeverity>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ThrowsAsync(new InvalidOperationException("boom"));

        await r.SendAsync(AlertSeverity.Critical, "t", "b", default);

        em .Verify(x => x.SendAsync(AlertSeverity.Critical, "t", "b", It.IsAny<CancellationToken>()), Times.Once);
        ai .Verify(x => x.SendAsync(AlertSeverity.Critical, "t", "b", It.IsAny<CancellationToken>()), Times.Once);
        journal.Verify(j => j.InsertAsync(It.Is<AlertJournalRow>(row => !row.Transports.Contains("Telegram") && row.Transports.Contains("Email")), It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

- [ ] **Step 2: Verify test fails**

Run: `dotnet test --filter "FullyQualifiedName~AlertRouterTests"`
Expected: COMPILE FAIL.

- [ ] **Step 3: Implement `AlertRouter`**

```csharp
// src/TradingBot.Observability/Alerts/AlertRouter.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Observability;
using TradingBot.Data.Abstractions;
using TradingBot.Observability.Alerts.Configuration;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Alerts;

public sealed class AlertRouter : IAlertSink
{
    private readonly IReadOnlyList<IAlertTransport> _transports;
    private readonly AlertDedupCache _dedup;
    private readonly IAlertJournalRepository _journal;
    private readonly IClock _clock;
    private readonly AlertRoutingOptions _opts;
    private readonly ITradingMetrics _metrics;
    private readonly ILogger<AlertRouter> _log;

    public AlertRouter(
        IEnumerable<IAlertTransport> transports,
        AlertDedupCache dedup,
        IAlertJournalRepository journal,
        IClock clock,
        IOptions<AlertRoutingOptions> opts,
        ITradingMetrics metrics,
        ILogger<AlertRouter> log)
    {
        _transports = transports.ToList();
        _dedup = dedup;
        _journal = journal;
        _clock = clock;
        _opts = opts.Value;
        _metrics = metrics;
        _log = log;
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

        await _journal.InsertAsync(new AlertJournalRow(
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
}
```

- [ ] **Step 4: Verify tests pass**

Run: `dotnet test --filter "FullyQualifiedName~AlertRouterTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/TradingBot.Observability/Alerts/AlertRouter.cs tests/TradingBot.Tests/Observability/AlertRouterTests.cs
git commit -m "feat(observability): add AlertRouter (IAlertSink) with dedup + fan-out + journal"
```

---

### Task 17: `RoutingWebSocketAlertSink` — bridge `IWebSocketAlertSink` → `IAlertSink`

**Files:**
- Create: `src/TradingBot.Observability/WebSocket/RoutingWebSocketAlertSink.cs`
- Modify: `src/TradingBot.Exchange/DependencyInjection/ExchangeServiceCollectionExtensions.cs`
- Optional remove: `src/TradingBot.Exchange/WebSocket/LoggingWebSocketAlertSink.cs` (kept for now; replaced in DI)

- [ ] **Step 1: Implement bridge**

```csharp
// src/TradingBot.Observability/WebSocket/RoutingWebSocketAlertSink.cs
using TradingBot.Exchange.Abstractions;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.WebSocket;

/// <summary>
/// Default IWebSocketAlertSink for production: bridges WS-specific alerts
/// into the central IAlertSink pipeline so dedup/journal/fan-out apply.
/// </summary>
public sealed class RoutingWebSocketAlertSink(IAlertSink alerts) : IWebSocketAlertSink
{
    public void RaiseStaleStream(StreamHealth health) =>
        _ = alerts.SendAsync(
            AlertSeverity.Critical,
            $"WebSocket stream stale: {health.StreamId}",
            $"Account={health.Account}; lastEvent={health.LastEventUtc:O}; reconnects={health.ReconnectCount}; lastError={health.LastError}",
            CancellationToken.None);

    public void RaiseListenKeyExpired(AccountType account) =>
        _ = alerts.SendAsync(
            AlertSeverity.Critical,
            $"Binance listenKey expired: {account}",
            $"Trading should pause until rotation completes for {account}.",
            CancellationToken.None);
}
```

- [ ] **Step 2: Replace registration in `ExchangeServiceCollectionExtensions`**

In `AddBinanceExchange`, change the line:

```csharp
services.AddSingleton<IWebSocketAlertSink, LoggingWebSocketAlertSink>();
```

to:

```csharp
// Default: route through central IAlertSink. If IAlertSink isn't registered
// (older test-only host), the resolution will fail at runtime — Worker always
// registers IAlertSink via AddObservability before subscribers run.
services.AddSingleton<IWebSocketAlertSink, RoutingWebSocketAlertSink>();
```

(`LoggingWebSocketAlertSink.cs` stays for tests that want a no-IAlertSink path, but is no longer the default.)

- [ ] **Step 3: Verify builds**

Run: `dotnet build`
Expected: zero warnings.

- [ ] **Step 4: Commit**

```bash
git add src/TradingBot.Observability/WebSocket src/TradingBot.Exchange/DependencyInjection
git commit -m "feat(observability): bridge IWebSocketAlertSink into IAlertSink"
```

---

## Phase 8 — Health checks

### Task 18: `ProcessAliveHealthCheck` + `KillSwitchHealthCheck` + `BinanceKillSwitchHealthCheck`

**Files:**
- Create: `src/TradingBot.Observability/HealthChecks/ProcessAliveHealthCheck.cs`
- Create: `src/TradingBot.Observability/HealthChecks/KillSwitchHealthCheck.cs`
- Create: `src/TradingBot.Observability/HealthChecks/BinanceKillSwitchHealthCheck.cs`
- Create: `tests/TradingBot.Tests/Observability/KillSwitchHealthCheckTests.cs`

- [ ] **Step 1: Implement `ProcessAliveHealthCheck`**

```csharp
// src/TradingBot.Observability/HealthChecks/ProcessAliveHealthCheck.cs
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TradingBot.Observability.HealthChecks;

public sealed class ProcessAliveHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
        => Task.FromResult(HealthCheckResult.Healthy("process alive"));
}
```

- [ ] **Step 2: Implement `KillSwitchHealthCheck`**

```csharp
// src/TradingBot.Observability/HealthChecks/KillSwitchHealthCheck.cs
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TradingBot.Risk.Abstractions;

namespace TradingBot.Observability.HealthChecks;

public sealed class KillSwitchHealthCheck(IKillSwitch killSwitch) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        if (!killSwitch.IsTripped)
            return Task.FromResult(HealthCheckResult.Healthy("kill switch off"));

        var data = new Dictionary<string, object>
        {
            ["source"]    = killSwitch.Source.ToString(),
            ["reason"]    = killSwitch.Reason ?? "(unknown)",
            ["trippedAt"] = killSwitch.TrippedAtUtc?.ToString("o") ?? string.Empty,
        };
        return Task.FromResult(HealthCheckResult.Unhealthy("kill switch tripped", data: data));
    }
}
```

- [ ] **Step 3: Implement `BinanceKillSwitchHealthCheck`**

```csharp
// src/TradingBot.Observability/HealthChecks/BinanceKillSwitchHealthCheck.cs
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TradingBot.Exchange.Abstractions;

namespace TradingBot.Observability.HealthChecks;

public sealed class BinanceKillSwitchHealthCheck(IBinanceKillSwitch ks) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        if (!ks.IsTripped)
            return Task.FromResult(HealthCheckResult.Healthy("binance kill switch off"));

        var data = new Dictionary<string, object>
        {
            ["reason"]        = ks.Reason ?? "(unknown)",
            ["trippedAt"]     = ks.TrippedAtUtc?.ToString("o") ?? string.Empty,
            ["retryAfterUtc"] = ks.RetryAfterUtc?.ToString("o") ?? string.Empty,
        };
        return Task.FromResult(HealthCheckResult.Unhealthy("binance kill switch tripped", data: data));
    }
}
```

- [ ] **Step 4: Add `TradingBot.Exchange` reference to `TradingBot.Observability.csproj`**

```xml
<ProjectReference Include="..\TradingBot.Exchange\TradingBot.Exchange.csproj" />
```

(Required for `IBinanceKillSwitch` and the `RoutingWebSocketAlertSink` bridge.)

- [ ] **Step 5: Write tests**

```csharp
// tests/TradingBot.Tests/Observability/KillSwitchHealthCheckTests.cs
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using TradingBot.Observability.HealthChecks;
using TradingBot.Risk.Abstractions;
using Xunit;

namespace TradingBot.Tests.Observability;

public class KillSwitchHealthCheckTests
{
    [Fact]
    public async Task Healthy_when_not_tripped()
    {
        var ks = new Mock<IKillSwitch>();
        ks.SetupGet(x => x.IsTripped).Returns(false);

        var result = await new KillSwitchHealthCheck(ks.Object)
            .CheckHealthAsync(new HealthCheckContext(), default);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Unhealthy_when_tripped_includes_source_and_reason_in_data()
    {
        var ks = new Mock<IKillSwitch>();
        ks.SetupGet(x => x.IsTripped).Returns(true);
        ks.SetupGet(x => x.Source).Returns(KillSwitchSource.DailyLossLimit);
        ks.SetupGet(x => x.Reason).Returns("daily loss > -3%");
        ks.SetupGet(x => x.TrippedAtUtc).Returns(DateTime.UtcNow);

        var result = await new KillSwitchHealthCheck(ks.Object)
            .CheckHealthAsync(new HealthCheckContext(), default);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data["source"].Should().Be("DailyLossLimit");
        result.Data["reason"].Should().Be("daily loss > -3%");
    }
}
```

- [ ] **Step 6: Verify tests pass**

Run: `dotnet test --filter "FullyQualifiedName~KillSwitchHealthCheckTests"`
Expected: PASS (2 tests).

- [ ] **Step 7: Commit**

```bash
git add src/TradingBot.Observability/HealthChecks src/TradingBot.Observability/TradingBot.Observability.csproj tests/TradingBot.Tests/Observability/KillSwitchHealthCheckTests.cs
git commit -m "feat(observability): add ProcessAlive + KillSwitch + BinanceKillSwitch health checks"
```

---

## Phase 9 — Digests

### Task 19: `IDailyAiCostReader` + impl + repository additions

**Files:**
- Create: `src/TradingBot.AI/Abstractions/IDailyAiCostReader.cs`
- Create: `src/TradingBot.AI/Cost/DailyAiCostReader.cs`
- Modify: `src/TradingBot.AI/DependencyInjection/AiServiceCollectionExtensions.cs` (register `IDailyAiCostReader`)
- Modify: `src/TradingBot.Data/Abstractions/IAccountSnapshotRepository.cs` (add `GetNearestAsync` if missing)
- Modify: `src/TradingBot.Data/Repositories/AccountSnapshotRepository.cs` (impl)
- Verify: `src/TradingBot.Data/Abstractions/ITradeHistoryRepository.cs` has `GetClosedAsync(since, until, ct)` — add if missing
- Verify: `src/TradingBot.Data/Abstractions/IPositionRepository.cs` has `GetOpenAsync(ct)` — add if missing

- [ ] **Step 1: Inspect existing repos**

Read `src/TradingBot.Data/Abstractions/IAccountSnapshotRepository.cs`, `ITradeHistoryRepository.cs`, `IPositionRepository.cs`. For each missing method, add the signature and Dapper impl following the existing repo style (use `dbo.AccountSnapshots`, `dbo.TradeHistory`, `dbo.Positions` schemas).

`IAccountSnapshotRepository.GetNearestAsync` SQL:
```sql
SELECT TOP 1 ... FROM dbo.AccountSnapshots WHERE TimestampUtc <= @at ORDER BY TimestampUtc DESC;
```

`ITradeHistoryRepository.GetClosedAsync` SQL:
```sql
SELECT ... FROM dbo.TradeHistory WHERE ClosedAtUtc >= @since AND ClosedAtUtc < @until ORDER BY ClosedAtUtc;
```

`IPositionRepository.GetOpenAsync` SQL:
```sql
SELECT ... FROM dbo.Positions WHERE Status = 'OPEN';
```

(Method names and column names confirmed via grep; if a similar method already exists with a different name, reuse it and skip the addition.)

- [ ] **Step 2: Define `IDailyAiCostReader`**

```csharp
// src/TradingBot.AI/Abstractions/IDailyAiCostReader.cs
namespace TradingBot.AI.Abstractions;

public interface IDailyAiCostReader
{
    Task<decimal> GetTotalForDayAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct);
}
```

- [ ] **Step 3: Implement `DailyAiCostReader`**

The existing `DailyCostMeter` is in-process only. The persistent record is the `dbo.AiCallJournal` (or whatever the cost-write table is named — verify via grep `INSERT INTO dbo.Ai`). The reader queries `SUM(CostUsd)` for the day window.

```csharp
// src/TradingBot.AI/Cost/DailyAiCostReader.cs
using Dapper;
using TradingBot.AI.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.AI.Cost;

public sealed class DailyAiCostReader(ISqlConnectionFactory cf) : IDailyAiCostReader
{
    public async Task<decimal> GetTotalForDayAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        const string sql = @"
SELECT COALESCE(SUM(CostUsd), 0)
FROM   dbo.AiCallJournal
WHERE  CalledAtUtc >= @startUtc AND CalledAtUtc < @endUtc;";

        using var conn = cf.Create();
        return await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(sql, new { startUtc, endUtc }, cancellationToken: ct));
    }
}
```

(If the actual table/column names differ, adjust the SQL. The implementation phase confirms.)

- [ ] **Step 4: Register in `AddAi`**

In `AiServiceCollectionExtensions.AddAi`, add:

```csharp
services.AddScoped<IDailyAiCostReader, DailyAiCostReader>();
```

- [ ] **Step 5: Build + run all tests**

Run: `dotnet build && dotnet test`
Expected: zero warnings, all tests green.

- [ ] **Step 6: Commit**

```bash
git add src/TradingBot.AI/Abstractions/IDailyAiCostReader.cs src/TradingBot.AI/Cost/DailyAiCostReader.cs src/TradingBot.AI/DependencyInjection src/TradingBot.Data
git commit -m "feat(data,ai): add IDailyAiCostReader + repo additions for digest queries"
```

---

### Task 20: `WarnDigestRenderer` + `WarnDigestJob`

**Files:**
- Create: `src/TradingBot.Observability/Digest/WarnDigestRenderer.cs`
- Create: `src/TradingBot.Observability/Digest/WarnDigestJob.cs`
- Create: `tests/TradingBot.Tests/Observability/WarnDigestRendererTests.cs`
- Create: `tests/TradingBot.Tests/Observability/WarnDigestJobTests.cs`

- [ ] **Step 1: Write failing renderer test**

```csharp
// tests/TradingBot.Tests/Observability/WarnDigestRendererTests.cs
using FluentAssertions;
using TradingBot.Data.Abstractions;
using TradingBot.Observability.Digest;
using Xunit;

namespace TradingBot.Tests.Observability;

public class WarnDigestRendererTests
{
    [Fact]
    public void Empty_input_yields_empty_string()
    {
        WarnDigestRenderer.Render(Array.Empty<AlertJournalRow>(), DateTime.UtcNow.AddHours(-6), DateTime.UtcNow)
            .Should().BeEmpty();
    }

    [Fact]
    public void Single_row_renders_one_bullet()
    {
        var t = DateTime.UtcNow;
        var rows = new[] { new AlertJournalRow(t, 1, "ai cap reached", "spent $2", new string('a',64), "Log", "bot", null) };
        var s = WarnDigestRenderer.Render(rows, t.AddHours(-6), t);
        s.Should().Contain("ai cap reached");
        s.Split('\n').Count(l => l.StartsWith("•")).Should().Be(1);
    }

    [Fact]
    public void Over_30_rows_truncates_with_more_footer()
    {
        var t = DateTime.UtcNow;
        var rows = Enumerable.Range(0, 35)
            .Select(i => new AlertJournalRow(t.AddMinutes(-i), 1, $"warn-{i}", "", new string('a',64), "Log", "bot", null))
            .ToArray();
        var s = WarnDigestRenderer.Render(rows, t.AddHours(-6), t);
        s.Should().Contain("+5 more");
    }
}
```

- [ ] **Step 2: Implement renderer**

```csharp
// src/TradingBot.Observability/Digest/WarnDigestRenderer.cs
using System.Text;
using TradingBot.Data.Abstractions;

namespace TradingBot.Observability.Digest;

public static class WarnDigestRenderer
{
    private const int MaxEntries = 30;
    private const int BodyExcerpt = 80;

    public static string Render(IReadOnlyList<AlertJournalRow> rows, DateTime sinceUtc, DateTime untilUtc)
    {
        if (rows.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"*WARN digest* — {rows.Count} alert(s) since {sinceUtc:HH:mm} UTC");
        sb.AppendLine();

        foreach (var row in rows.Take(MaxEntries))
        {
            var excerpt = row.Body.Length > BodyExcerpt
                ? row.Body[..BodyExcerpt] + "…"
                : row.Body;
            sb.AppendLine($"• {row.SentAtUtc:HH:mm} {row.Title} — {excerpt}");
        }

        if (rows.Count > MaxEntries)
            sb.AppendLine($"…+{rows.Count - MaxEntries} more");

        return sb.ToString();
    }
}
```

- [ ] **Step 3: Verify renderer tests pass**

Run: `dotnet test --filter "FullyQualifiedName~WarnDigestRendererTests"`
Expected: PASS (3 tests).

- [ ] **Step 4: Write failing job test**

```csharp
// tests/TradingBot.Tests/Observability/WarnDigestJobTests.cs
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Quartz;
using TradingBot.Core.Abstractions;
using TradingBot.Data.Abstractions;
using TradingBot.Observability.Alerts;
using TradingBot.Observability.Alerts.Configuration;
using TradingBot.Observability.Digest;
using Xunit;

namespace TradingBot.Tests.Observability;

public class WarnDigestJobTests
{
    private sealed class FixedClock(DateTime t) : IClock { public DateTime UtcNow => t; }

    [Fact]
    public async Task Empty_journal_does_not_call_telegram()
    {
        var journal = new Mock<IAlertJournalRepository>();
        journal.Setup(x => x.GetWindowAsync(1, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(Array.Empty<AlertJournalRow>());
        var tg = new Mock<ITelegramSender>();

        var job = new WarnDigestJob(journal.Object, tg.Object,
            Options.Create(new AlertRoutingOptions()),
            Options.Create(new TelegramOptions { WarnChatId = "warn-chat" }),
            new FixedClock(DateTime.UtcNow), NullLogger<WarnDigestJob>.Instance);

        await job.Execute(new Mock<IJobExecutionContext>().Object);

        tg.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Populated_journal_calls_telegram_once_with_warn_chat_id()
    {
        var t = DateTime.UtcNow;
        var rows = new[] { new AlertJournalRow(t, 1, "ai cap reached", "x", new string('a',64), "Log", "bot", null) };
        var journal = new Mock<IAlertJournalRepository>();
        journal.Setup(x => x.GetWindowAsync(1, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(rows);
        var tg = new Mock<ITelegramSender>();

        var job = new WarnDigestJob(journal.Object, tg.Object,
            Options.Create(new AlertRoutingOptions()),
            Options.Create(new TelegramOptions { WarnChatId = "warn-chat" }),
            new FixedClock(t), NullLogger<WarnDigestJob>.Instance);

        await job.Execute(new Mock<IJobExecutionContext>().Object);

        tg.Verify(x => x.SendAsync("warn-chat", It.Is<string>(s => s.Contains("ai cap reached")), It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

- [ ] **Step 5: Implement `WarnDigestJob`**

```csharp
// src/TradingBot.Observability/Digest/WarnDigestJob.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using TradingBot.Core.Abstractions;
using TradingBot.Data.Abstractions;
using TradingBot.Observability.Alerts;
using TradingBot.Observability.Alerts.Configuration;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Digest;

[DisallowConcurrentExecution]
public sealed class WarnDigestJob(
    IAlertJournalRepository journal,
    ITelegramSender telegram,
    IOptions<AlertRoutingOptions> routing,
    IOptions<TelegramOptions> tg,
    IClock clock,
    ILogger<WarnDigestJob> log) : IJob
{
    public const string JobKey = "warn-digest-job";

    public async Task Execute(IJobExecutionContext context)
    {
        var now = clock.UtcNow;
        var since = now - routing.Value.WarnDigestInterval;

        var rows = await journal.GetWindowAsync((byte)AlertSeverity.Warn, since, now, context.CancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0) return;
        if (string.IsNullOrWhiteSpace(tg.Value.WarnChatId))
        {
            log.LogDebug("WarnDigestJob: WarnChatId empty, skipping telegram send");
            return;
        }

        var body = WarnDigestRenderer.Render(rows, since, now);
        await telegram.SendAsync(tg.Value.WarnChatId, body, context.CancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 6: Verify job tests pass**

Run: `dotnet test --filter "FullyQualifiedName~WarnDigestJobTests"`
Expected: PASS (2 tests).

- [ ] **Step 7: Commit**

```bash
git add src/TradingBot.Observability/Digest/WarnDigestRenderer.cs src/TradingBot.Observability/Digest/WarnDigestJob.cs tests/TradingBot.Tests/Observability/WarnDigestRendererTests.cs tests/TradingBot.Tests/Observability/WarnDigestJobTests.cs
git commit -m "feat(observability): add WarnDigest renderer + Quartz job"
```

---

### Task 21: `DigestData` + `DigestRenderer` (HTML)

**Files:**
- Create: `src/TradingBot.Observability/Digest/DigestData.cs`
- Create: `src/TradingBot.Observability/Digest/DigestRenderer.cs`
- Create: `tests/TradingBot.Tests/Observability/DigestRendererTests.cs`
- Create: `tests/TradingBot.Tests/Observability/golden/daily_digest_basic.html` (committed fixture)

- [ ] **Step 1: Define `DigestData` record**

```csharp
// src/TradingBot.Observability/Digest/DigestData.cs
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;

namespace TradingBot.Observability.Digest;

public sealed record DigestData(
    DateTime DayStartUtc,
    DateTime DayEndUtc,
    IReadOnlyList<TradeRecord>     ClosedTrades,
    IReadOnlyList<Position>        OpenPositions,
    AccountSnapshot?               EquityStart,
    AccountSnapshot?               EquityEnd,
    IReadOnlyList<AlertJournalRow> AlertRows,
    decimal                        AiCostUsd);
```

(Use the existing `TradeRecord`, `Position`, `AccountSnapshot` types from `TradingBot.Core.Domain` / `TradingBot.Data.Abstractions`. If type names differ, substitute the actual ones — they live next to the existing repo interfaces.)

- [ ] **Step 2: Implement `DigestRenderer`**

```csharp
// src/TradingBot.Observability/Digest/DigestRenderer.cs
using System.Net;
using System.Text;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Digest;

public sealed class DigestRenderer
{
    public string RenderHtml(DigestData d)
    {
        var sb = new StringBuilder();
        sb.Append("<html><body style='font-family:Arial,sans-serif;font-size:13px;'>");
        sb.Append($"<h2>TradingBot daily digest — {d.DayStartUtc:yyyy-MM-dd} UTC</h2>");

        AppendEquity(sb, d);
        AppendClosedTrades(sb, d);
        AppendOpenPositions(sb, d);
        AppendAlerts(sb, d);
        AppendAiCost(sb, d);

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void AppendEquity(StringBuilder sb, DigestData d)
    {
        var start = d.EquityStart?.EquityUsd ?? 0m;
        var end   = d.EquityEnd?.EquityUsd   ?? 0m;
        var delta = end - start;
        sb.Append("<h3>Equity</h3>");
        sb.Append("<table border='1' cellpadding='4' cellspacing='0'>");
        sb.Append("<tr><th>Open</th><th>Close</th><th>Δ</th></tr>");
        sb.Append($"<tr><td>{start:C}</td><td>{end:C}</td><td>{delta:C}</td></tr>");
        sb.Append("</table>");
    }

    private static void AppendClosedTrades(StringBuilder sb, DigestData d)
    {
        sb.Append($"<h3>Closed trades ({d.ClosedTrades.Count})</h3>");
        if (d.ClosedTrades.Count == 0)
        {
            sb.Append("<p>No trades closed today.</p>");
            return;
        }

        sb.Append("<table border='1' cellpadding='4' cellspacing='0'>");
        sb.Append("<tr><th>Symbol</th><th>Side</th><th>Qty</th><th>PnL</th></tr>");
        foreach (var t in d.ClosedTrades)
            sb.Append($"<tr><td>{Esc(t.Symbol)}</td><td>{Esc(t.Side.ToString())}</td><td>{t.Quantity}</td><td>{t.RealizedPnlUsd:C}</td></tr>");
        sb.Append("</table>");
    }

    private static void AppendOpenPositions(StringBuilder sb, DigestData d)
    {
        sb.Append($"<h3>Open positions ({d.OpenPositions.Count})</h3>");
        if (d.OpenPositions.Count == 0)
        {
            sb.Append("<p>No open positions.</p>");
            return;
        }

        sb.Append("<table border='1' cellpadding='4' cellspacing='0'>");
        sb.Append("<tr><th>Symbol</th><th>Qty</th><th>Avg entry</th><th>Unrealized PnL</th></tr>");
        foreach (var p in d.OpenPositions)
            sb.Append($"<tr><td>{Esc(p.Symbol)}</td><td>{p.Quantity}</td><td>{p.AvgEntryPrice}</td><td>{p.UnrealizedPnlUsd:C}</td></tr>");
        sb.Append("</table>");
    }

    private static void AppendAlerts(StringBuilder sb, DigestData d)
    {
        var bySev = d.AlertRows.GroupBy(a => a.Severity).OrderByDescending(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        sb.Append("<h3>Alerts</h3>");
        if (d.AlertRows.Count == 0) { sb.Append("<p>No alerts in the last 24h.</p>"); return; }

        sb.Append("<table border='1' cellpadding='4' cellspacing='0'>");
        sb.Append("<tr><th>Severity</th><th>Count</th></tr>");
        foreach (var sev in new[] { (byte)AlertSeverity.Critical, (byte)AlertSeverity.Error, (byte)AlertSeverity.Warn, (byte)AlertSeverity.Info })
            sb.Append($"<tr><td>{(AlertSeverity)sev}</td><td>{(bySev.TryGetValue(sev, out var c) ? c : 0)}</td></tr>");
        sb.Append("</table>");

        var critical = d.AlertRows.Where(a => a.Severity >= (byte)AlertSeverity.Error).Take(20).ToList();
        if (critical.Count > 0)
        {
            sb.Append("<h4>Notable (Error / Critical)</h4><ul>");
            foreach (var a in critical)
                sb.Append($"<li>{a.SentAtUtc:HH:mm} — {Esc(a.Title)}</li>");
            sb.Append("</ul>");
        }
    }

    private static void AppendAiCost(StringBuilder sb, DigestData d)
    {
        sb.Append($"<h3>AI cost</h3><p>{d.AiCostUsd:C} spent on Claude calls.</p>");
    }

    private static string Esc(string s) => WebUtility.HtmlEncode(s);
}
```

- [ ] **Step 3: Write golden-file test**

```csharp
// tests/TradingBot.Tests/Observability/DigestRendererTests.cs
using FluentAssertions;
using TradingBot.Observability.Digest;
using Xunit;

namespace TradingBot.Tests.Observability;

public class DigestRendererTests
{
    [Fact]
    public void Empty_data_renders_zero_state_blocks()
    {
        var d = new DigestData(
            DayStartUtc: new DateTime(2026, 5, 8, 0, 0, 0, DateTimeKind.Utc),
            DayEndUtc:   new DateTime(2026, 5, 9, 0, 0, 0, DateTimeKind.Utc),
            ClosedTrades:  Array.Empty<TradingBot.Core.Domain.TradeRecord>(),
            OpenPositions: Array.Empty<TradingBot.Core.Domain.Position>(),
            EquityStart:   null, EquityEnd: null,
            AlertRows:     Array.Empty<TradingBot.Data.Abstractions.AlertJournalRow>(),
            AiCostUsd:     0m);

        var html = new DigestRenderer().RenderHtml(d);

        html.Should().Contain("<h2>TradingBot daily digest — 2026-05-08 UTC</h2>");
        html.Should().Contain("No trades closed today.");
        html.Should().Contain("No open positions.");
        html.Should().Contain("No alerts in the last 24h.");
    }

    [Fact]
    public void Title_strings_are_html_escaped()
    {
        var t = DateTime.UtcNow;
        var rows = new[] { new TradingBot.Data.Abstractions.AlertJournalRow(t, 3, "<script>x</script>", "b", new string('a',64), "Log", "bot", null) };
        var d = new DigestData(t.Date, t.Date.AddDays(1), Array.Empty<TradingBot.Core.Domain.TradeRecord>(), Array.Empty<TradingBot.Core.Domain.Position>(), null, null, rows, 0m);

        var html = new DigestRenderer().RenderHtml(d);

        html.Should().Contain("&lt;script&gt;");
        html.Should().NotContain("<script>x</script>");
    }
}
```

- [ ] **Step 4: Verify tests pass**

Run: `dotnet test --filter "FullyQualifiedName~DigestRendererTests"`
Expected: PASS (2 tests). If domain type names (e.g. `TradeRecord`, `Position`) differ from what's used here, adjust the test fixtures and `DigestData` record accordingly.

- [ ] **Step 5: Commit**

```bash
git add src/TradingBot.Observability/Digest/DigestData.cs src/TradingBot.Observability/Digest/DigestRenderer.cs tests/TradingBot.Tests/Observability/DigestRendererTests.cs
git commit -m "feat(observability): add DigestData + DigestRenderer (HTML)"
```

---

### Task 22: `DailyDigestJob`

**Files:**
- Create: `src/TradingBot.Observability/Digest/DailyDigestJob.cs`
- Create: `tests/TradingBot.Tests/Observability/DailyDigestJobTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/TradingBot.Tests/Observability/DailyDigestJobTests.cs
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Quartz;
using TradingBot.AI.Abstractions;
using TradingBot.Core.Abstractions;
using TradingBot.Data.Abstractions;
using TradingBot.Observability.Alerts;
using TradingBot.Observability.Alerts.Configuration;
using TradingBot.Observability.Digest;
using Xunit;

namespace TradingBot.Tests.Observability;

public class DailyDigestJobTests
{
    private sealed class FixedClock(DateTime t) : IClock { public DateTime UtcNow => t; }

    [Fact]
    public async Task Empty_To_list_skips_email_send()
    {
        var (alerts, trades, positions, snapshots, aiCost, email, t) = NewMocks();
        var opts = Options.Create(new SendGridOptions { Enabled = true, To = new List<string>() });
        var job = new DailyDigestJob(alerts.Object, trades.Object, positions.Object, snapshots.Object, aiCost.Object, email.Object, opts, new FixedClock(t), new DigestRenderer(), NullLogger<DailyDigestJob>.Instance);

        await job.Execute(new Mock<IJobExecutionContext>().Object);

        email.Verify(e => e.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sends_email_with_subject_containing_yesterdays_date()
    {
        var (alerts, trades, positions, snapshots, aiCost, email, t) = NewMocks();
        var opts = Options.Create(new SendGridOptions { Enabled = true, To = new List<string> { "x@y.io" } });
        var job = new DailyDigestJob(alerts.Object, trades.Object, positions.Object, snapshots.Object, aiCost.Object, email.Object, opts, new FixedClock(t), new DigestRenderer(), NullLogger<DailyDigestJob>.Instance);

        await job.Execute(new Mock<IJobExecutionContext>().Object);

        email.Verify(e => e.SendAsync(
            It.Is<string>(s => s.Contains((t.Date.AddDays(-1)).ToString("yyyy-MM-dd"))),
            It.IsAny<string>(),
            It.Is<IEnumerable<string>>(list => list.Contains("x@y.io")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static (Mock<IAlertJournalRepository> alerts, Mock<ITradeHistoryRepository> trades,
                    Mock<IPositionRepository> positions, Mock<IAccountSnapshotRepository> snapshots,
                    Mock<IDailyAiCostReader> aiCost, Mock<IEmailSender> email, DateTime now)
        NewMocks()
    {
        var t = new DateTime(2026, 5, 9, 6, 0, 0, DateTimeKind.Utc);
        var alerts = new Mock<IAlertJournalRepository>();
        alerts.Setup(x => x.GetWindowAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<AlertJournalRow>());
        var trades = new Mock<ITradeHistoryRepository>();
        trades.Setup(x => x.GetClosedAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<TradingBot.Core.Domain.TradeRecord>());
        var positions = new Mock<IPositionRepository>();
        positions.Setup(x => x.GetOpenAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(Array.Empty<TradingBot.Core.Domain.Position>());
        var snapshots = new Mock<IAccountSnapshotRepository>();
        snapshots.Setup(x => x.GetNearestAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((TradingBot.Data.Abstractions.AccountSnapshot?)null);
        var aiCost = new Mock<IDailyAiCostReader>();
        aiCost.Setup(x => x.GetTotalForDayAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(0m);
        var email = new Mock<IEmailSender>();
        return (alerts, trades, positions, snapshots, aiCost, email, t);
    }
}
```

- [ ] **Step 2: Implement `DailyDigestJob`**

```csharp
// src/TradingBot.Observability/Digest/DailyDigestJob.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using TradingBot.AI.Abstractions;
using TradingBot.Core.Abstractions;
using TradingBot.Data.Abstractions;
using TradingBot.Observability.Alerts;
using TradingBot.Observability.Alerts.Configuration;

namespace TradingBot.Observability.Digest;

[DisallowConcurrentExecution]
public sealed class DailyDigestJob(
    IAlertJournalRepository alerts,
    ITradeHistoryRepository trades,
    IPositionRepository positions,
    IAccountSnapshotRepository snapshots,
    IDailyAiCostReader aiCost,
    IEmailSender email,
    IOptions<SendGridOptions> sg,
    IClock clock,
    DigestRenderer renderer,
    ILogger<DailyDigestJob> log) : IJob
{
    public const string JobKey = "daily-digest-job";

    public async Task Execute(IJobExecutionContext context)
    {
        if (sg.Value.To.Count == 0)
        {
            log.LogWarning("DailyDigestJob skipped: SendGrid:To is empty");
            return;
        }

        var ct = context.CancellationToken;
        var now    = clock.UtcNow;
        var dayEnd   = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
        var dayStart = dayEnd.AddDays(-1);

        var data = new DigestData(
            DayStartUtc:   dayStart,
            DayEndUtc:     dayEnd,
            ClosedTrades:  await trades.GetClosedAsync(dayStart, dayEnd, ct).ConfigureAwait(false),
            OpenPositions: await positions.GetOpenAsync(ct).ConfigureAwait(false),
            EquityStart:   await snapshots.GetNearestAsync(dayStart, ct).ConfigureAwait(false),
            EquityEnd:     await snapshots.GetNearestAsync(dayEnd,   ct).ConfigureAwait(false),
            AlertRows:     await alerts.GetWindowAsync(null, dayStart, dayEnd, ct).ConfigureAwait(false),
            AiCostUsd:     await aiCost.GetTotalForDayAsync(dayStart, dayEnd, ct).ConfigureAwait(false));

        var html    = renderer.RenderHtml(data);
        var subject = $"TradingBot daily digest — {dayStart:yyyy-MM-dd}";
        await email.SendAsync(subject, html, sg.Value.To, ct).ConfigureAwait(false);
    }
}
```

- [ ] **Step 3: Verify tests pass**

Run: `dotnet test --filter "FullyQualifiedName~DailyDigestJobTests"`
Expected: PASS (2 tests).

- [ ] **Step 4: Commit**

```bash
git add src/TradingBot.Observability/Digest/DailyDigestJob.cs tests/TradingBot.Tests/Observability/DailyDigestJobTests.cs
git commit -m "feat(observability): add DailyDigestJob (Quartz, 06:00 UTC)"
```

---

## Phase 10 — `AddObservability` + Worker wiring

### Task 23: Comprehensive `AddObservability` extension

**Files:**
- Modify: `src/TradingBot.Observability/DependencyInjection/ObservabilityServiceCollectionExtensions.cs`

- [ ] **Step 1: Replace stub with full implementation**

```csharp
// src/TradingBot.Observability/DependencyInjection/ObservabilityServiceCollectionExtensions.cs
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quartz;
using Serilog.Core;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Observability;
using TradingBot.Observability.Alerts;
using TradingBot.Observability.Alerts.Configuration;
using TradingBot.Observability.Alerts.Transports;
using TradingBot.Observability.Digest;
using TradingBot.Observability.HealthChecks;
using TradingBot.Observability.Logging;
using TradingBot.Observability.Metrics;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.DependencyInjection;

public static class ObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        ISecretsProvider bootstrapSecrets)
    {
        // -- Logging enrichers (picked up by Serilog .ReadFrom.Services) --
        services.AddOptions<SensitiveLoggingOptions>()
            .Bind(configuration.GetSection(SensitiveLoggingOptions.SectionName));
        services.AddSingleton<ILogEventEnricher, CorrelationIdEnricher>();
        services.AddSingleton<ILogEventEnricher, SensitiveDataEnricher>();

        // -- Metrics --
        services.AddSingleton<ITradingMetrics, PrometheusTradingMetrics>();

        // -- Alert routing core --
        services.AddOptions<AlertRoutingOptions>()
            .Bind(configuration.GetSection(AlertRoutingOptions.SectionName));
        services.AddSingleton<AlertDedupCache>();
        services.AddSingleton<IAlertSink, AlertRouter>();

        // -- Always-on transport --
        services.AddSingleton<IAlertTransport, LoggingAlertTransport>();

        // -- Telegram (flagged) --
        services.AddOptions<TelegramOptions>()
            .Bind(configuration.GetSection(TelegramOptions.SectionName))
            .Validate(o => !o.Enabled || (!string.IsNullOrEmpty(o.CriticalChatId) && !string.IsNullOrEmpty(o.WarnChatId)),
                "Telegram chat IDs must be set when Telegram:Enabled=true");

        var telegramEnabled = configuration.GetValue($"{TelegramOptions.SectionName}:Enabled", false);
        if (telegramEnabled)
        {
            var tokenName = configuration[$"{TelegramOptions.SectionName}:BotTokenSecretName"] ?? "Telegram:BotToken";
            var token = bootstrapSecrets.GetAsync(tokenName).GetAwaiter().GetResult()
                        ?? throw new InvalidOperationException($"Telegram:Enabled=true but secret '{tokenName}' is unset");

            services.AddHttpClient<ITelegramSender, TelegramSender>(c => c.BaseAddress = new Uri("https://api.telegram.org/"))
                .AddTypedClient<ITelegramSender>((http, sp) =>
                    new TelegramSender(http, sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TelegramOptions>>(), token,
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TelegramSender>>()));

            services.AddSingleton<IAlertTransport, TelegramAlertTransport>();
        }

        // -- SendGrid (flagged) --
        services.AddOptions<SendGridOptions>()
            .Bind(configuration.GetSection(SendGridOptions.SectionName));

        var sendGridEnabled = configuration.GetValue($"{SendGridOptions.SectionName}:Enabled", false);
        if (sendGridEnabled)
        {
            var keyName = configuration[$"{SendGridOptions.SectionName}:ApiKeySecretName"] ?? "SendGrid:ApiKey";
            var apiKey = bootstrapSecrets.GetAsync(keyName).GetAwaiter().GetResult()
                         ?? throw new InvalidOperationException($"SendGrid:Enabled=true but secret '{keyName}' is unset");

            services.AddHttpClient<IEmailSender, SendGridEmailSender>(c => c.BaseAddress = new Uri("https://api.sendgrid.com/"))
                .AddTypedClient<IEmailSender>((http, sp) =>
                    new SendGridEmailSender(http, sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SendGridOptions>>(), apiKey,
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SendGridEmailSender>>()));

            services.AddSingleton<IAlertTransport, SendGridAlertTransport>();
        }

        // -- App Insights (flagged) --
        services.AddOptions<AppInsightsOptions>()
            .Bind(configuration.GetSection(AppInsightsOptions.SectionName));

        var aiEnabled = configuration.GetValue($"{AppInsightsOptions.SectionName}:Enabled", false);
        if (aiEnabled)
        {
            var connName = configuration[$"{AppInsightsOptions.SectionName}:ConnectionStringSecretName"] ?? "AppInsights:ConnectionString";
            var conn = bootstrapSecrets.GetAsync(connName).GetAwaiter().GetResult()
                       ?? throw new InvalidOperationException($"AppInsights:Enabled=true but secret '{connName}' is unset");

            services.AddSingleton(_ =>
            {
                var cfg = TelemetryConfiguration.CreateDefault();
                cfg.ConnectionString = conn;
                return new TelemetryClient(cfg);
            });
            services.AddSingleton<IAlertTransport, AppInsightsAlertTransport>();
        }

        // -- Health checks (registered as DI types so AddHealthChecks().AddCheck<T>() resolves them) --
        services.AddSingleton<ProcessAliveHealthCheck>();
        services.AddSingleton<KillSwitchHealthCheck>();
        services.AddSingleton<BinanceKillSwitchHealthCheck>();
        services.AddSingleton<WebSocket.RoutingWebSocketAlertSink>();

        // -- Quartz digest jobs (additive registration; AddQuartz is idempotent) --
        services.AddQuartz(q =>
        {
            // WARN digest, every 6h.
            if (telegramEnabled)
            {
                var key = new JobKey(WarnDigestJob.JobKey);
                q.AddJob<WarnDigestJob>(o => o.WithIdentity(key).StoreDurably());
                q.AddTrigger(t => t.ForJob(key)
                    .WithIdentity(WarnDigestJob.JobKey + "-trigger")
                    .WithCronSchedule("0 0 0/6 ? * *", c => c.InTimeZone(TimeZoneInfo.Utc)));
            }

            // Daily digest at 06:00 UTC.
            if (sendGridEnabled)
            {
                var cron = configuration[$"{AlertRoutingOptions.SectionName}:DailyDigestCronUtc"] ?? "0 0 6 ? * *";
                var key  = new JobKey(DailyDigestJob.JobKey);
                q.AddJob<DailyDigestJob>(o => o.WithIdentity(key).StoreDurably());
                q.AddTrigger(t => t.ForJob(key)
                    .WithIdentity(DailyDigestJob.JobKey + "-trigger")
                    .WithCronSchedule(cron, c => c.InTimeZone(TimeZoneInfo.Utc)));
            }
        });

        // -- Required by digest jobs --
        services.AddSingleton<DigestRenderer>();

        return services;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: zero warnings.

- [ ] **Step 3: Commit**

```bash
git add src/TradingBot.Observability/DependencyInjection/ObservabilityServiceCollectionExtensions.cs
git commit -m "feat(observability): wire AddObservability comprehensive registration"
```

---

### Task 24: Worker `Program.cs` rewire (health checks, MapMetrics, /admin/test-alert, AddObservability)

**Files:**
- Modify: `src/TradingBot.Worker/Program.cs`
- Modify: `src/TradingBot.Worker/TradingBot.Worker.csproj` (add Observability project ref)
- Create: `tests/TradingBot.Tests/Observability/HealthEndpointsIntegrationTests.cs`
- Create: `tests/TradingBot.Tests/Observability/MetricsEndpointIntegrationTests.cs`

- [ ] **Step 1: Add project ref to Worker csproj**

```xml
<ProjectReference Include="..\TradingBot.Observability\TradingBot.Observability.csproj" />
```

- [ ] **Step 2: Rewire Program.cs**

Replace the Telegram options block with the new Observability call. After `AddRisk(builder.Configuration)` in the existing registration list, add:

```csharp
builder.Services.AddObservability(builder.Configuration, bootstrapSecrets);
```

(`bootstrapSecrets` is already constructed earlier in `Program.cs`.)

Replace the existing health-check block:

```csharp
var hcBuilder = builder.Services.AddHealthChecks()
    .AddCheck<ProcessAliveHealthCheck>("process",                       tags: ["live"])
    .AddCheck<BinancePingHealthCheck>("binance",                        tags: ["ready"])
    .AddCheck<WebSocketHealthCheck>("websocket",                        tags: ["ready"])
    .AddCheck<KillSwitchHealthCheck>("killswitch",                      tags: ["ready"])
    .AddCheck<BinanceKillSwitchHealthCheck>("binance_killswitch",       tags: ["ready"]);

if (!string.IsNullOrWhiteSpace(dbConn))
    hcBuilder.AddSqlServer(dbConn, name: "sqlserver", tags: ["ready"]);
```

Add the necessary `using TradingBot.Observability.HealthChecks;` and `using TradingBot.Observability.DependencyInjection;` directives.

Replace the endpoint mappings:

```csharp
app.MapHealthChecks("/health",            new() { ResponseWriter = WriteHealthResponse });
app.MapHealthChecks("/health/liveness",   new() { Predicate = r => r.Tags.Contains("live"),  ResponseWriter = WriteHealthResponse });
app.MapHealthChecks("/health/readiness",  new() { Predicate = r => r.Tags.Contains("ready"), ResponseWriter = WriteHealthResponse });

app.MapMetrics();   // Prometheus exposition at /metrics
```

Add the env-gated test endpoint after `MapNewsfeedPush()`:

```csharp
if (!builder.Environment.IsProduction())
{
    app.MapPost("/admin/test-alert",
        async (TestAlertRequest req, IAlertSink alerts, CancellationToken ct) =>
        {
            await alerts.SendAsync(req.Severity, req.Title, req.Body ?? "Test alert", ct);
            return Results.Accepted();
        });
}
```

At the bottom of `Program.cs` (where `public partial class Program;` is defined), add the request type:

```csharp
internal sealed record TestAlertRequest(TradingBot.Risk.KillSwitch.AlertSeverity Severity, string Title, string? Body);
```

- [ ] **Step 3: Write integration test for health endpoints**

```csharp
// tests/TradingBot.Tests/Observability/HealthEndpointsIntegrationTests.cs
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TradingBot.Risk.Abstractions;
using Xunit;

namespace TradingBot.Tests.Observability;

public class HealthEndpointsIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public HealthEndpointsIntegrationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Liveness_returns_healthy_independent_of_external_services()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health/liveness");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_unhealthy_when_kill_switch_tripped()
    {
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            var ks = new Mock<IKillSwitch>();
            ks.SetupGet(x => x.IsTripped).Returns(true);
            ks.SetupGet(x => x.Source).Returns(KillSwitchSource.ManualCommand);
            ks.SetupGet(x => x.Reason).Returns("test");
            ks.SetupGet(x => x.TrippedAtUtc).Returns(DateTime.UtcNow);

            // Replace the singleton.
            for (var i = s.Count - 1; i >= 0; i--)
                if (s[i].ServiceType == typeof(IKillSwitch)) s.RemoveAt(i);
            s.AddSingleton(ks.Object);
        }));

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/health/readiness");
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("killswitch");
    }
}
```

- [ ] **Step 4: Write integration test for /metrics**

```csharp
// tests/TradingBot.Tests/Observability/MetricsEndpointIntegrationTests.cs
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace TradingBot.Tests.Observability;

public class MetricsEndpointIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public MetricsEndpointIntegrationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Metrics_endpoint_exposes_all_tradingbot_families()
    {
        using var client = _factory.CreateClient();
        var text = await client.GetStringAsync("/metrics");

        var families = new[]
        {
            "tradingbot_signals_total",
            "tradingbot_orders_total",
            "tradingbot_orders_filled_total",
            "tradingbot_orders_canceled_total",
            "tradingbot_position_pnl_usd",
            "tradingbot_account_equity_usd",
            "tradingbot_drawdown_pct",
            "tradingbot_ai_calls_total",
            "tradingbot_ai_cost_usd_total",
            "tradingbot_ws_reconnects_total",
            "tradingbot_strategy_latency_ms",
            "tradingbot_order_fill_latency_ms",
            "tradingbot_ws_last_event_seconds",
            "tradingbot_alerts_deduped_total",
        };
        foreach (var name in families)
            text.Should().Contain(name);
    }
}
```

- [ ] **Step 5: Build + run integration tests**

Run: `dotnet build && dotnet test --filter "FullyQualifiedName~HealthEndpoints|FullyQualifiedName~MetricsEndpoint"`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add src/TradingBot.Worker tests/TradingBot.Tests/Observability/HealthEndpointsIntegrationTests.cs tests/TradingBot.Tests/Observability/MetricsEndpointIntegrationTests.cs
git commit -m "feat(worker): wire AddObservability + rename health endpoints + MapMetrics + /admin/test-alert"
```

---

## Phase 11 — Alert call-site wiring

### Task 25: Wire `IAlertSink` into BinanceKillSwitch / RiskManager / ReconciliationService / DailyCostMeter

**Files:**
- Modify: `src/TradingBot.Exchange/Resilience/BinanceKillSwitch.cs`
- Modify: `src/TradingBot.Risk/Manager/RiskManager.cs`
- Modify: `src/TradingBot.Execution/Reconciliation/ReconciliationService.cs`
- Modify: `src/TradingBot.AI/Cost/DailyCostMeter.cs`

`KillSwitch` already accepts an optional `IAlertSink`; once `AddObservability` registers `IAlertSink`, that wiring activates with no code change. The four files below need explicit additions.

- [ ] **Step 1: BinanceKillSwitch — accept optional IAlertSink, raise CRITICAL on Trip**

In `BinanceKillSwitch.cs`:
1. Add ctor param `IAlertSink? alerts = null`.
2. In `Trip(string reason, DateTime? retryAfterUtc)`, after the existing state mutation, fire-and-forget:

```csharp
_ = _alerts?.SendAsync(
    AlertSeverity.Critical,
    "Binance kill switch tripped",
    $"reason={reason}; retryAfterUtc={retryAfterUtc?.ToString("o") ?? "(none)"}",
    CancellationToken.None);
```

- [ ] **Step 2: RiskManager — direct alert on daily-loss / max-DD halt**

In `RiskManager.cs`, find the daily-loss-limit and max-drawdown branches (they currently log `LogCritical` and call `_killSwitch.TripAsync`). The KillSwitch alert path covers them already — but where the halt is raised *without* a kill-switch trip (e.g. soft-cap reject), add:

```csharp
_ = _alerts?.SendAsync(AlertSeverity.Critical,
    "Risk halt: daily loss limit",
    $"realized={realizedPnl:C} limit={limit:C}",
    CancellationToken.None);
```

(Add ctor param `IAlertSink? alerts = null` if not present.)

- [ ] **Step 3: ReconciliationService — ERROR alert on drift trip**

In `ReconciliationService.cs`, near the existing `if (driftUsd > _options.DriftTripUsd)` block (around line 254 of current file), after the existing severity decision, raise:

```csharp
_ = _alerts?.SendAsync(AlertSeverity.Error,
    "Reconciliation drift trip",
    $"position={pos.PositionId} symbol={sym.SymbolCode} dbQty={pos.Quantity} exQty={eq} driftUsd={driftUsd:F2}",
    ct);
```

(Add ctor param `IAlertSink? alerts = null` if not present.)

- [ ] **Step 4: DailyCostMeter — WARN on cap reached**

`DailyCostMeter.Record(decimal)` currently has no alert. Modify to:

```csharp
public void Record(decimal spentUsd)
{
    if (spentUsd <= 0m) return;
    bool capJustReached;
    lock (_lock)
    {
        RollOverIfNewDay();
        var wasUnderCap = _spentToday < _capUsd;
        _spentToday += spentUsd;
        capJustReached = wasUnderCap && _spentToday >= _capUsd;
    }
    _metrics.AddAiCost("unknown", (double)spentUsd);
    if (capJustReached)
    {
        _ = _alerts?.SendAsync(AlertSeverity.Warn,
            "AI daily cost cap reached",
            $"spent={_spentToday:C} cap={_capUsd:C}",
            CancellationToken.None);
    }
}
```

(Add ctor params `IAlertSink? alerts = null` and `ITradingMetrics metrics`. The `ITradingMetrics` is required from Task 10 anyway; this task makes it explicit. The "unknown" purpose label stays a known limitation — full purpose plumbing is out of scope.)

- [ ] **Step 5: Build + run all tests**

Run: `dotnet build && dotnet test`
Expected: zero warnings, all tests green. Existing KillSwitch / DailyCostMeter / RiskManager tests construct these with positional args — they continue to compile because the new params have defaults.

- [ ] **Step 6: Commit**

```bash
git add src/TradingBot.Exchange/Resilience/BinanceKillSwitch.cs src/TradingBot.Risk/Manager/RiskManager.cs src/TradingBot.Execution/Reconciliation/ReconciliationService.cs src/TradingBot.AI/Cost/DailyCostMeter.cs
git commit -m "feat(alerts): wire IAlertSink into 4 critical event call-sites"
```

---

## Phase 12 — Artifacts and docs

### Task 26: Grafana dashboard JSON

**Files:**
- Create: `dashboards/grafana/tradingbot.json`

- [ ] **Step 1: Write dashboard JSON**

```json
{
  "annotations": { "list": [] },
  "editable": true,
  "schemaVersion": 38,
  "title": "TradingBot",
  "tags": ["tradingbot"],
  "templating": {
    "list": [
      {
        "name": "DS_PROMETHEUS",
        "label": "Prometheus",
        "type": "datasource",
        "query": "prometheus"
      }
    ]
  },
  "time": { "from": "now-24h", "to": "now" },
  "panels": [
    {
      "id": 1, "type": "timeseries", "title": "Equity (USD)",
      "datasource": { "type": "prometheus", "uid": "${DS_PROMETHEUS}" },
      "gridPos": { "x": 0, "y": 0, "w": 12, "h": 8 },
      "targets": [{ "expr": "tradingbot_account_equity_usd", "refId": "A" }]
    },
    {
      "id": 2, "type": "timeseries", "title": "Drawdown %",
      "datasource": { "type": "prometheus", "uid": "${DS_PROMETHEUS}" },
      "gridPos": { "x": 12, "y": 0, "w": 12, "h": 8 },
      "targets": [{ "expr": "tradingbot_drawdown_pct", "refId": "A" }],
      "fieldConfig": { "defaults": { "thresholds": { "mode": "absolute", "steps": [
        { "color": "red", "value": -0.10 }, { "color": "green", "value": null }
      ]}}}
    },
    {
      "id": 3, "type": "table", "title": "Open positions PnL",
      "datasource": { "type": "prometheus", "uid": "${DS_PROMETHEUS}" },
      "gridPos": { "x": 0, "y": 8, "w": 12, "h": 6 },
      "targets": [{ "expr": "tradingbot_position_pnl_usd", "refId": "A", "instant": true, "format": "table" }]
    },
    {
      "id": 4, "type": "stat", "title": "WS last event (s)",
      "datasource": { "type": "prometheus", "uid": "${DS_PROMETHEUS}" },
      "gridPos": { "x": 12, "y": 8, "w": 6, "h": 6 },
      "targets": [{ "expr": "max(tradingbot_ws_last_event_seconds)", "refId": "A" }],
      "fieldConfig": { "defaults": { "thresholds": { "mode": "absolute", "steps": [
        { "color": "green", "value": null }, { "color": "red", "value": 30 }
      ]}}}
    },
    {
      "id": 5, "type": "timeseries", "title": "Order fill latency (ms)",
      "datasource": { "type": "prometheus", "uid": "${DS_PROMETHEUS}" },
      "gridPos": { "x": 18, "y": 8, "w": 6, "h": 6 },
      "targets": [
        { "expr": "histogram_quantile(0.50, sum(rate(tradingbot_order_fill_latency_ms_bucket[5m])) by (le))", "refId": "A", "legendFormat": "p50" },
        { "expr": "histogram_quantile(0.95, sum(rate(tradingbot_order_fill_latency_ms_bucket[5m])) by (le))", "refId": "B", "legendFormat": "p95" },
        { "expr": "histogram_quantile(0.99, sum(rate(tradingbot_order_fill_latency_ms_bucket[5m])) by (le))", "refId": "C", "legendFormat": "p99" }
      ]
    },
    {
      "id": 6, "type": "piechart", "title": "AI cost by purpose (24h)",
      "datasource": { "type": "prometheus", "uid": "${DS_PROMETHEUS}" },
      "gridPos": { "x": 0, "y": 14, "w": 12, "h": 8 },
      "targets": [{ "expr": "sum by (purpose)(increase(tradingbot_ai_cost_usd_total[24h]))", "refId": "A", "legendFormat": "{{purpose}}" }]
    },
    {
      "id": 7, "type": "barchart", "title": "Daily P&L (last 14d)",
      "datasource": { "type": "prometheus", "uid": "${DS_PROMETHEUS}" },
      "gridPos": { "x": 12, "y": 14, "w": 12, "h": 8 },
      "targets": [{ "expr": "delta(tradingbot_account_equity_usd[1d])", "refId": "A" }]
    }
  ]
}
```

- [ ] **Step 2: Verify JSON**

Run: `pwsh -c "Get-Content dashboards/grafana/tradingbot.json | ConvertFrom-Json | Out-Null; Write-Output 'OK'"`
Expected: `OK`.

- [ ] **Step 3: Commit**

```bash
git add dashboards/grafana/tradingbot.json
git commit -m "feat(observability): add Grafana dashboard JSON"
```

---

### Task 27: n8n workflow JSON — news ingest

**Files:**
- Create: `n8n/workflow_news_ingest.json`

- [ ] **Step 1: Write workflow JSON**

```json
{
  "name": "tradingbot-news-ingest",
  "nodes": [
    {
      "id": "schedule",
      "name": "Every 5 min",
      "type": "n8n-nodes-base.scheduleTrigger",
      "typeVersion": 1,
      "position": [240, 300],
      "parameters": {
        "rule": { "interval": [{ "field": "minutes", "minutesInterval": 5 }] }
      }
    },
    {
      "id": "fetch",
      "name": "CryptoPanic GET",
      "type": "n8n-nodes-base.httpRequest",
      "typeVersion": 4,
      "position": [480, 300],
      "parameters": {
        "url": "=https://cryptopanic.com/api/v1/posts/?auth_token={{$credentials.cryptopanic.token}}&kind=news",
        "method": "GET",
        "responseFormat": "json"
      },
      "credentials": { "cryptopanicApi": { "id": "cryptopanic", "name": "cryptopanic" } }
    },
    {
      "id": "filter",
      "name": "Filter new posts",
      "type": "n8n-nodes-base.code",
      "typeVersion": 2,
      "position": [720, 300],
      "parameters": {
        "jsCode": "const last = $getWorkflowStaticData('global').lastRun || '1970-01-01T00:00:00Z';\nconst items = $input.first().json.results || [];\nconst kept = items.filter(i => i.published_at > last);\nif (kept.length) $getWorkflowStaticData('global').lastRun = kept[0].published_at;\nreturn kept.map(i => ({ json: i }));"
      }
    },
    {
      "id": "post",
      "name": "POST to bot",
      "type": "n8n-nodes-base.httpRequest",
      "typeVersion": 4,
      "position": [960, 300],
      "parameters": {
        "url": "={{$env.TRADINGBOT_NEWS_WEBHOOK_URL}}/newsfeed/push",
        "method": "POST",
        "sendHeaders": true,
        "headerParameters": {
          "parameters": [
            { "name": "X-Webhook-Secret", "value": "={{$credentials.tradingbot.webhookSecret}}" },
            { "name": "Content-Type",     "value": "application/x-ndjson" }
          ]
        },
        "sendBody": true,
        "specifyBody": "json",
        "jsonBody": "={{$json}}"
      }
    }
  ],
  "connections": {
    "Every 5 min":     { "main": [[{ "node": "CryptoPanic GET",   "type": "main", "index": 0 }]] },
    "CryptoPanic GET": { "main": [[{ "node": "Filter new posts", "type": "main", "index": 0 }]] },
    "Filter new posts":{ "main": [[{ "node": "POST to bot",      "type": "main", "index": 0 }]] }
  }
}
```

- [ ] **Step 2: Verify JSON**

Run: `pwsh -c "Get-Content n8n/workflow_news_ingest.json | ConvertFrom-Json | Out-Null; Write-Output 'OK'"`
Expected: `OK`.

- [ ] **Step 3: Commit**

```bash
git add n8n/workflow_news_ingest.json
git commit -m "feat(observability): add n8n news-ingest workflow"
```

---

### Task 28: Smoke-test doc

**Files:**
- Create: `docs/section11-smoke-test.md`

- [ ] **Step 1: Write the doc**

```markdown
# Section 11 — Smoke test recipes

Manual verification of observability + alerting end-to-end. Each recipe is independent.

## Prerequisites

- `dotnet run --project src/TradingBot.Worker` running on `http://localhost:5080`.
- For live alert tests: `Telegram:Enabled=true` + valid `Telegram:BotToken` user-secret + non-empty `Telegram:CriticalChatId`. Same for `SendGrid:Enabled=true` + `SendGrid:ApiKey` + `SendGrid:To`.

## 1. Verify CorrelationId enricher

```bash
curl -X POST http://localhost:5080/admin/test-alert \
  -H 'Content-Type: application/json' \
  -d '{"severity":"Critical","title":"CorrId smoke","body":"verify CorrelationId field"}'
```

Tail `logs/bot-*.log` — the corresponding ALERT line should NOT have a `CorrelationId` property (the test endpoint runs outside any signal scope). Compare to a real signal log line during normal operation, which DOES have `CorrelationId`.

## 2. Verify sensitive-data redaction

Push a structured event from the worker shell (or via `dotnet user-secrets set "Binance:ApiKey" "leak-test-value"` followed by a startup) — the startup-banner log line that mentions secrets should print `***REDACTED***` for the `ApiKey` property.

## 3. Verify `/metrics`

```bash
curl -s http://localhost:5080/metrics | grep '^tradingbot_' | sort -u
```

Expected: 14 unique family names beginning with `tradingbot_`.

## 4. Verify health endpoints

```bash
curl -i http://localhost:5080/health/liveness     # → 200 always (process up)
curl -i http://localhost:5080/health/readiness    # → 200 when DB/Binance/WS reachable + KillSwitch off
```

Trip the kill switch via a fake daily-loss event (or by tripping it via a one-off SQL call), then re-curl readiness — expect `503 Service Unavailable` with JSON containing `"name":"killswitch"`.

## 5. Live CRITICAL → Telegram + Email

```bash
curl -X POST http://localhost:5080/admin/test-alert \
  -H 'Content-Type: application/json' \
  -d '{"severity":"Critical","title":"S11 smoke","body":"this is a test"}'
```

Within 10s:
- Telegram message arrives at `CriticalChatId`.
- Email arrives at every address in `SendGrid:To`.

## 6. WARN digest

```bash
for i in 1 2 3; do
  curl -X POST http://localhost:5080/admin/test-alert \
    -H 'Content-Type: application/json' \
    -d "{\"severity\":\"Warn\",\"title\":\"warn-$i\",\"body\":\"sample\"}"
done
```

Wait for the next 6h Quartz trigger (or temporarily change `Alerts:WarnDigestInterval` to `00:00:30` for testing). One Telegram message should arrive at `WarnChatId` listing all three.

## 7. Daily digest

Temporarily set `Alerts:DailyDigestCronUtc` to `0 0/2 * ? * *` (every 2 minutes) and restart the bot. Within 2 minutes, an email should arrive at `SendGrid:To` with subject `TradingBot daily digest — yyyy-MM-dd`. Restore the original cron (`0 0 6 ? * *`) after testing.

## 8. Grafana import

In Grafana, import `dashboards/grafana/tradingbot.json` and select your Prometheus datasource when prompted. Within 1 minute of bot activity, every panel should populate with data.
```

- [ ] **Step 2: Commit**

```bash
git add docs/section11-smoke-test.md
git commit -m "docs(s11): add section11-smoke-test.md"
```

---

### Task 29: CLAUDE.md + README updates

**Files:**
- Modify: `CLAUDE.md`
- Modify: `README.md`

- [ ] **Step 1: CLAUDE.md updates**

In `CLAUDE.md`:

1. In the "Common commands" block, replace:
   ```
   curl http://localhost:5080/health/ready     # binance + sqlserver
   curl http://localhost:5080/health/live      # binance + websocket
   ```
   with:
   ```
   curl http://localhost:5080/health/readiness # sqlserver + binance + websocket + killswitch
   curl http://localhost:5080/health/liveness  # process alive (always Healthy unless host dying)
   curl http://localhost:5080/metrics          # Prometheus exposition
   ```

2. In the "Architecture" bullet list, add an entry between **TradingBot.Backtest** and **TradingBot.Worker**:

   ```
   - **TradingBot.Observability** — Serilog enrichers (`SignalContext`/CorrelationId, sensitive-data redaction), `ITradingMetrics` Prometheus impl, `IAlertSink` router with 5-min dedup + journal, transports (Logging, Telegram, SendGrid, AppInsights — all flagged off by default), Quartz-driven WARN (6h) and daily (06:00 UTC) digests, `KillSwitch` / `BinanceKillSwitch` / `ProcessAlive` health checks. Cross-cutting; Worker calls `AddObservability(...)` after `AddRisk(...)`.
   ```

3. In the "Configuration & secrets" section, replace the line about `Telegram:BotToken` with:

   ```
   Telegram, SendGrid, and App Insights are wired in §11 and feature-flagged off by default. Set `Telegram:Enabled=true` (plus `Telegram:BotToken` user-secret + chat IDs) to send alerts; set `SendGrid:Enabled=true` (plus `SendGrid:ApiKey` user-secret + `SendGrid:To`) for email; set `AppInsights:Enabled=true` (plus `AppInsights:ConnectionString` user-secret) for Azure App Insights. Vault key names use `--`: `Telegram--BotToken`, `SendGrid--ApiKey`, `AppInsights--ConnectionString`.
   ```

- [ ] **Step 2: README.md updates**

In `README.md`, replace `/health/live` → `/health/liveness` and `/health/ready` → `/health/readiness`. Add a one-line mention of `/metrics` next to the existing `/health` curl example.

- [ ] **Step 3: Build + run all tests one more time**

Run: `dotnet build && dotnet test`
Expected: zero warnings, all tests green.

- [ ] **Step 4: Commit**

```bash
git add CLAUDE.md README.md
git commit -m "docs(s11): update CLAUDE.md + README for observability paths/secrets"
```

---

## Self-review (run after the plan is implemented)

After Task 29 is complete, verify:

1. `dotnet build` — zero warnings, `TreatWarningsAsErrors=true`.
2. `dotnet test` — all ~42 new tests across 17 new test files + the existing suite green.
3. `curl http://localhost:5080/metrics` returns all 14 `tradingbot_*` families.
4. `curl http://localhost:5080/health/liveness` returns 200 even when DB / Binance unreachable.
5. `curl http://localhost:5080/health/readiness` returns 503 when KillSwitch is tripped, with reason in JSON.
6. With `Telegram:Enabled=true` + `SendGrid:Enabled=true`, Smoke recipes 5–8 pass.
7. Default `dotnet run` works in dev with no Telegram / SendGrid / AppInsights credentials configured.
8. Grafana import + 1-min data verification.

If any step fails, the implementation is incomplete; do not declare DoD.

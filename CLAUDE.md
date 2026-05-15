# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Binance AI-assisted automated trading system on .NET 8. Single solution `TradingBot.sln`; the composition root is `src/TradingBot.Worker` (a `WebApplication` host that owns all DI, config layering, hosted services, and the `/health*` + `/newsfeed/push` endpoints). Implementation is being built in numbered sections (S1–S11+); the canonical system design lives in `compass_artifact_*.md` and per-section verification steps are in `docs/section*-smoke-test.md`.

## Common commands

```powershell
dotnet build                                         # zero warnings expected (TreatWarningsAsErrors=true)
dotnet test                                          # xUnit + FluentAssertions + Moq + Testcontainers.MsSql
dotnet run --project src/TradingBot.Worker          # starts host on http://localhost:5080

# Single test (filter by FullyQualifiedName)
dotnet test --filter "FullyQualifiedName~TradingBot.Tests.AI" --no-build
dotnet test --filter "FullyQualifiedName=TradingBot.Tests.AI.ClaudeClientTests.SomeTest"

# Reset + migrate local dev DB (idempotent — drops, recreates, applies all sql/migrations/*.sql via DbUp)
pwsh ./Make-DevDb.ps1                               # default: (localdb)\MSSQLLocalDB / TradingDb
pwsh ./Make-DevDb.ps1 -Server '.\SQLEXPRESS'
pwsh ./Make-DevDb.ps1 -ConnectionString '...'

# Migrations directly (without reset)
dotnet run --project src/TradingBot.MigrationsRunner -- --connection "<conn>"
dotnet run --project src/TradingBot.MigrationsRunner -- --reset --connection "<conn>"

# Backtest CLI (standalone executable, separate from Worker host)
dotnet run --project src/TradingBot.Backtest -- run --strategy <CODE> --symbol <CODE> --from <UTC> --to <UTC>
dotnet run --project src/TradingBot.Backtest -- wfa --strategy <CODE> --symbol <CODE> --from <UTC> --to <UTC> [--is-months 6 --oos-months 1 --step-months 1]
dotnet run --project src/TradingBot.Backtest -- mc  --runId <id> [--reshuffles 1000 --skips 100]

# Health checks once running
curl http://localhost:5080/health           # full diagnostic
curl http://localhost:5080/health/readiness # sqlserver + binance + websocket + killswitch
curl http://localhost:5080/health/liveness  # process alive (always Healthy unless host dying)
curl http://localhost:5080/metrics          # Prometheus exposition (§11)
```

Some integration tests spin up a real SQL Server via Testcontainers (`tests/.../Database/SqlServerFixture.cs`) — Docker must be running for those. `LiveSentimentIntegrationTests` is skipped unless `ANTHROPIC_API_KEY` env var is set.

## Configuration & secrets

Configuration precedence (lowest → highest), wired by `AddSecretsSources` in `Program.cs`:

1. `appsettings.json` → `appsettings.{Env}.json`
2. .NET User Secrets (Development only)
3. Azure Key Vault (non-Dev, when `KeyVault:Uri` is set; uses `DefaultAzureCredential`)
4. Environment variables — section separator is `__` (e.g. `Bot__InstanceId`)
5. Command-line args

Vault key names use `--` as the section separator (e.g. `Binance--ApiKey`, `Anthropic--ApiKey`, `Database--ConnectionString`, `Telegram--BotToken`, `SendGrid--ApiKey`, `AppInsights--ConnectionString`). All secret reads go through `ISecretsProvider` (default `ConfigurationSecretsProvider`); secrets must never be committed to disk-tracked files. Set local dev secrets with `dotnet user-secrets set ...` from `src/TradingBot.Worker`.

§11 alert providers are flagged off by default — set `Telegram:Enabled=true` (plus `Telegram:BotToken` user-secret + chat IDs) to send Telegram alerts; set `SendGrid:Enabled=true` (plus `SendGrid:ApiKey` user-secret + `SendGrid:To` recipient list) for email; set `AppInsights:Enabled=true` (plus `AppInsights:ConnectionString` user-secret) for Azure App Insights. `src/TradingBot.Backtest` shares the Worker's `UserSecretsId`, so a single `dotnet user-secrets set "Database:ConnectionString" ...` configures both binaries.

Strongly-typed options (`BotOptions`, `BinanceOptions`, `AnthropicOptions`, `ClaudeOptions`, `DatabaseOptions`, `KeyVaultOptions`, `TelegramOptions`, etc.) bind via `AddOptions<T>().Bind(...).ValidateDataAnnotations().ValidateOnStart()` — invalid config fails the host at startup.

## Architecture

Layered, with each `src/TradingBot.*` library exposing a single `Add<Module>(IServiceCollection, IConfiguration, ...)` extension that the Worker calls in `Program.cs`. Cross-layer dependencies only go inward toward `TradingBot.Core`.

- **TradingBot.Core** — domain types (`Order`, `Position`, `Signal`, `Candle`, `RegimeRecord`, `NewsSentimentRecord`, `AiJournalRecord`, …), abstractions (`IClock`, `ISecretsProvider`), shared indicators, `Domain/Enums` (incl. `CodeConstants`).
- **TradingBot.Data** — EF Core / Dapper repositories (`IOrderRepository`, `IPositionRepository`, `IAccountSnapshotRepository`, `ITradeHistoryRepository`, …) and the DbUp migrator. SQL DDL lives in `sql/migrations/NNN_*.sql` and is applied at Worker startup by `DatabaseMigrationStartupService` and by the standalone `TradingBot.MigrationsRunner` console app.
- **TradingBot.Exchange** — Binance.Net wrappers: `BinanceSpotGateway`, `BinanceFuturesGateway`, `BinanceGatewayResolver`, REST ping + WS health probe, key-permission verifier, exchange filters/reference data, Polly resilience policies.
- **TradingBot.MarketData** — candle ingestion, persistence, gap detection/backfill, indicator window, optional Redis live-candle cache; subscriptions configured under `MarketData:Subscriptions`.
- **TradingBot.Strategies** — `BreakoutDonchianStrategy`, `MeanReversionBbVwapStrategy`, `TrendEmaAdxStrategy`, `SignalEngine`, `MarketContextBuilder`, regime classification + strategy selection, bracket calculator.
- **TradingBot.Risk** — `RiskManager` (gate between strategy signals and execution), correlation cluster checks, funding-rate veto, kill-switch / drawdown limits, account snapshot + pricing helpers. Tunables under `Risk:` in `appsettings.json`.
- **TradingBot.Execution** — order state machine, intent channel, `ExecutionEngine`, `SignalApprovalHostedService`, `UserDataReactor`, brackets/trailing/slippage helpers, reconciliation. Tunables under `Execution:`.
- **TradingBot.AI** — Claude client (`ClaudeClient`, `ClaudeBatchClient`), prompt cache (`AiResponseCache`), `DailyCostMeter` + `TokenBucketRateLimiter`, prompt rendering (`SystemPrompts`, `UserPromptRenderer`), and the four AI roles defined in `Abstractions/AiPurposes.cs`: `ISetupConfirmer`, `IRegimeConfirmer`, `INewsSentimentAnalyzer`, `IPostTradeJournalist`. News sources: CryptoPanic, RSS, in-memory webhook (POST `/newsfeed/push`, optionally guarded by `News:WebhookSharedSecret`). Quartz schedules journal + correlation refresh jobs.
- **TradingBot.Observability** — §11 cross-cutting: Serilog enrichers (`SignalContext`/CorrelationId, sensitive-data redaction), `ITradingMetrics` Prometheus impl (14 instruments), `IAlertSink` router with 5-min in-memory dedup + `dbo.AlertJournal` persistence, transports (Logging always-on; Telegram/SendGrid/AppInsights feature-flagged off by default), Quartz-driven WARN (6h) and daily (06:00 UTC) digests, `ProcessAlive`/`KillSwitch`/`BinanceKillSwitch` health checks, `RoutingWebSocketAlertSink` bridge. Worker calls `AddObservability(...)` after `AddRisk(...)`. `ITradingMetrics` lives in `TradingBot.Core.Observability` so consuming modules don't take a dep on Observability; `NullTradingMetrics` is the default for tests.
- **TradingBot.Backtest** — replay + walk-forward + Monte Carlo (S10). **Standalone executable** (not loaded into the Worker host) with its own `Program.cs` CLI: `bt run | wfa | mc`. Reuses pure live components (`OrderStateMachine`, `BracketCalculator`, `DefaultSlippageModel`, `IIndicatorEngine`, `IRegimeClassifier`, all three `IStrategy` impls, `RiskMath`) but does not drive `ExecutionEngine` / `UserDataReactor` / `SignalEngine` BackgroundServices — replay is synchronous to preserve bit-exact determinism. Backtest output (signals, orders, fills, positions, trades, snapshots) writes to a parallel `bt.*` schema, all rows tagged by `BacktestRunId`; the run header lives in `dbo.BacktestRuns`, with WFA folds in `dbo.WalkForwardFolds` and per-iteration MC results in `dbo.MonteCarloResults`. Per-run artifacts (`equity.csv`, `drawdown.csv`, `metrics.json`, `report.md`) go to `backtest-output/run-{id:D8}/`.
- **TradingBot.Worker** — composition root only: configuration layering, Serilog, options validation, `Add*` registrations in dependency order, hosted services (migrations first), health checks, minimal-API endpoints.

Project-reference rule: `TradingBot.AI` references both `Core` and `Data`, and uses `FrameworkReference Microsoft.AspNetCore.App` so the news webhook can map directly into the Worker's `WebApplication`. `TradingBot.Tests` has `InternalsVisibleTo` access to `TradingBot.AI`.

## Build conventions

`Directory.Build.props` enforces repo-wide: `net8.0`, `LangVersion=12.0`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true` (NU190x security advisories excepted), `EnforceCodeStyleInBuild=true`, `InvariantGlobalization=true`, `Deterministic=true`. Three projects override `InvariantGlobalization=false` because `Microsoft.Data.SqlClient` 5.x requires ICU at runtime: `src/TradingBot.Worker`, `src/TradingBot.Backtest`, and `tests/TradingBot.Tests`.

## Logging

Three Serilog sinks: Console, rolling File (`logs/bot-yyyyMMdd.log`, 30-day retention), and Seq at `http://localhost:5341` (harmless when Seq isn't running). `Microsoft.AspNetCore` and `System.Net.Http` default to `Warning`.

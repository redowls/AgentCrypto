# TradingBot

Binance AI-Assisted Automated Trading System (.NET 8). See `compass_artifact_*.md` for the full system design.

## Status

**Section S1 — Solution scaffolding + config + secrets management**. No business logic yet. Only DI plumbing, layered configuration, secrets seam, Serilog, and a `/health` endpoint.

## Prerequisites

- Windows 10+, macOS, or Linux
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (`dotnet --version` ≥ 8.0)
- (Optional) [Seq](https://datalust.co/download) running locally on `http://localhost:5341` for structured log inspection
- (Optional, for S2+) SQL Server (Developer Edition or LocalDB)
- A Binance **testnet** account with API keys: <https://testnet.binance.vision/>

## Solution layout

```
TradingBot.sln
├── src/
│   ├── TradingBot.Core           Domain types, abstractions (IClock, ISecretsProvider)
│   ├── TradingBot.Data           EF Core DbContext (entities arrive in S2)
│   ├── TradingBot.Exchange       Binance.Net wrappers, ping, WS health probe
│   ├── TradingBot.Strategies     Strategy modules (S6)
│   ├── TradingBot.Risk           Risk gate (S7)
│   ├── TradingBot.Execution      Order state machine, Polly (S8)
│   ├── TradingBot.AI             Claude client, prompt cache (S9)
│   ├── TradingBot.Backtest       Replay + WFA + Monte Carlo (S10)
│   └── TradingBot.Worker         Hosted services + /health endpoint (composition root)
└── tests/
    └── TradingBot.Tests          xUnit + FluentAssertions + Moq
```

## Run locally

```bash
# Restore + build (zero warnings expected)
dotnet build

# Run the worker
dotnet run --project src/TradingBot.Worker

# In another shell, hit the health + metrics endpoints
curl http://localhost:5080/health           # full diagnostic
curl http://localhost:5080/health/liveness  # process alive
curl http://localhost:5080/health/readiness # DB + Binance + WS + KillSwitch
curl http://localhost:5080/metrics          # Prometheus exposition (§11)
```

You should see a `Bot host started` log line and `/health` should return JSON with at least the `binance` and `websocket` checks. The `binance` check pings Binance testnet REST; if your network blocks it you'll see `Unhealthy`.

## Set secrets via `dotnet user-secrets` (Development)

Secrets are **never** read from disk-committed files. In dev they come from .NET User Secrets, which are stored under `%APPDATA%/Microsoft/UserSecrets/<UserSecretsId>/secrets.json` (Windows) or `~/.microsoft/usersecrets/...` (Linux/macOS) — outside the repo.

```bash
cd src/TradingBot.Worker

# Binance testnet keys (required for the binance health check to authenticate)
dotnet user-secrets set "Binance:ApiKey"    "<your-testnet-api-key>"
dotnet user-secrets set "Binance:ApiSecret" "<your-testnet-api-secret>"

# Anthropic (only required from S9 onward)
dotnet user-secrets set "Anthropic:ApiKey" "<your-anthropic-key>"

# Telegram (only required from S11.3 onward)
dotnet user-secrets set "Telegram:BotToken" "<your-bot-token>"

# Optional: SQL Server connection string (S2 onward)
dotnet user-secrets set "Database:ConnectionString" "Server=(localdb)\\MSSQLLocalDB;Database=TradingDb;Trusted_Connection=True;TrustServerCertificate=True"
```

List configured secrets:

```bash
dotnet user-secrets list
```

## Production secrets (Azure Key Vault)

In `Production`, secrets come from Azure Key Vault using `DefaultAzureCredential` (managed identity in Azure, `az login` locally). Set the vault URI in `appsettings.Production.json` or via env var:

```bash
export KeyVault__Uri="https://my-trading-kv.vault.azure.net/"
```

Vault key naming uses `--` as the section separator (e.g. `Binance--ApiKey`, `Binance--ApiSecret`, `Anthropic--ApiKey`, `Telegram--BotToken`, `Database--ConnectionString`).

## Configuration precedence (lowest → highest)

1. `appsettings.json`
2. `appsettings.{Environment}.json`
3. .NET User Secrets (Development only)
4. Azure Key Vault (non-Development, when `KeyVault:Uri` is set)
5. Environment variables (`__` is the section separator, e.g. `Bot__InstanceId`)
6. Command-line arguments

## Run tests

```bash
dotnet test
```

## Logging

Three sinks are wired by default:
- **Console** — for `dotnet run` / Docker stdout
- **File** — `logs/bot-yyyyMMdd.log`, rolling daily, 30-day retention
- **Seq** — structured queryable UI at `http://localhost:5341` (override via `Serilog:WriteTo:2:Args:serverUrl`)

The Seq sink is harmless when Seq isn't running (Serilog buffers and drops on flush failure).

## Health endpoints

- `GET /health` — all checks
- `GET /health/ready` — readiness (Binance REST, SQL Server when configured)
- `GET /health/live` — liveness (Binance REST, WebSocket probe)

The WebSocket probe is a stub in S1 (always `Healthy` with status `NotStarted`); the real probe wires in S3.2.

## Next section

S2 — Database layer (DDL, EF Core entities, migrations, partition function for `Candles`).

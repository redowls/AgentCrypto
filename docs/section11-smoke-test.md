# Section 11 — Smoke test recipes

Manual verification of the observability + alerting end-to-end. Each recipe is independent.

## Prerequisites

- `dotnet run --project src/TradingBot.Worker` running on `http://localhost:5080`.
- For live alert tests: `Telegram:Enabled=true` + valid `Telegram:BotToken` user-secret + non-empty `Telegram:CriticalChatId`. Same for `SendGrid:Enabled=true` + `SendGrid:ApiKey` + `SendGrid:To`.

## 1. CorrelationId enricher

```bash
curl -X POST http://localhost:5080/admin/test-alert \
  -H 'Content-Type: application/json' \
  -d '{"severity":"Critical","title":"CorrId smoke","body":"verify CorrelationId field"}'
```

Tail `logs/bot-*.log` — the corresponding `ALERT` line should NOT have a `CorrelationId` property (the test endpoint runs outside any signal scope). Compare to a real signal log line during normal operation, which DOES have `CorrelationId`.

## 2. Sensitive-data redaction

Log a structured event from the worker shell with a property called `ApiKey` (or use `dotnet user-secrets set "Binance:ApiKey" "leak-test-value"` and restart). The startup-banner log line that mentions secrets should show `***REDACTED***` for the `ApiKey` property.

## 3. Verify `/metrics`

```bash
curl -s http://localhost:5080/metrics | grep '^tradingbot_' | awk '{print $1}' | sort -u
```

Expected: all 14 `tradingbot_*` family names. The `_bucket`/`_count`/`_sum` series for histograms count as one family each.

## 4. Health endpoints

```bash
curl -i http://localhost:5080/health/liveness     # → 200 always (process up)
curl -i http://localhost:5080/health/readiness    # → 200 when DB/Binance/WS reachable + KillSwitch off
curl -s http://localhost:5080/health              # → full diagnostic JSON
```

Trip the kill switch via SQL or by lowering `Risk:DailyLossLimitPct` to a tiny value, then re-curl readiness — expect `503 Service Unavailable` with JSON containing `"name":"killswitch"` and details about the trip.

## 5. Live CRITICAL → Telegram + Email

```bash
curl -X POST http://localhost:5080/admin/test-alert \
  -H 'Content-Type: application/json' \
  -d '{"severity":"Critical","title":"S11 smoke","body":"this is a test"}'
```

Within 10s:
- Telegram message arrives at `CriticalChatId`.
- Email arrives at every address in `SendGrid:To`.

The `dbo.AlertJournal` row written for this alert should list `Transports="Log,Telegram,Email"` (or `"Log,Telegram,Email,AppInsights"` when App Insights is also enabled).

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

Temporarily set `Alerts:DailyDigestCronUtc` to `0 0/2 * ? * *` (every 2 minutes) and restart the bot. Within 2 minutes, an email should arrive at `SendGrid:To` with subject `TradingBot daily digest — yyyy-MM-dd` containing equity, closed trades, open positions, alert summary, and AI cost. Restore the original cron (`0 0 6 ? * *`) after testing.

## 8. Grafana import

In Grafana, import `dashboards/grafana/tradingbot.json` and select your Prometheus datasource when prompted. Within 1 minute of bot activity, every panel should populate with data.

## Cleanup

Remove any temporary cron changes and any test alert rows from `dbo.AlertJournal`:

```sql
DELETE FROM dbo.AlertJournal WHERE Title LIKE 'warn-%' OR Title LIKE 'S11 smoke' OR Title LIKE 'CorrId smoke';
```

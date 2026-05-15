# Section 9 — AI Layer smoke test

This doc walks through verifying every Definition-of-Done item for §S9.

## Prerequisites

| Secret | How to set |
|--------|------------|
| Anthropic API key | `dotnet user-secrets set Anthropic:ApiKey sk-ant-...` (run from `src/TradingBot.Worker`) |
| (optional) CryptoPanic token | `dotnet user-secrets set CryptoPanic:ApiKey ...` and set `News:EnableCryptoPanic=true` |
| (optional) Webhook shared secret | put a random string in `News:WebhookSharedSecret` |

The Worker reads secrets through `ISecretsProvider` — Key Vault in prod, user-secrets in dev. The API key never appears in logs; it's stamped on the `HttpClient` at construction time only.

## 1. Build + unit tests

```powershell
dotnet build
dotnet test --filter "FullyQualifiedName~TradingBot.Tests.AI" --no-build
```

Expected: 37 passed, 1 skipped (`LiveSentimentIntegrationTests` skips when `ANTHROPIC_API_KEY` is unset).

## 2. Migration 008 lands cleanly

```powershell
./Make-DevDb.ps1
```

Verify the two new tables exist:

```sql
SELECT name FROM sys.tables WHERE name IN ('NewsSentiment','AiJournals');
SELECT TOP 3 * FROM dbo.NewsSentiment;       -- empty until first ingest
SELECT TOP 3 * FROM dbo.AiJournals;          -- empty until first Sunday
```

## 3. Live sentiment call (Definition of Done #1)

Set the env var and run only the live test:

```powershell
$env:ANTHROPIC_API_KEY = "sk-ant-..."
dotnet test --filter "FullyQualifiedName~LiveSentimentIntegrationTests" --no-build
```

Expected: one real Anthropic call producing schema-valid NDJSON, asserting:
- `sentiment ∈ [-1, 1]`, `confidence ∈ [0, 1]`
- `horizon ∈ {INTRADAY, SWING, LONG}`

Cost: ≤ $0.01.

## 4. Cache hit prevents duplicate calls (Definition of Done #2)

Either trust the unit test
(`ClaudeClientTests.Local_cache_hit_short_circuits_second_call`) or watch it
live: with the bot running, hit the webhook twice with the same headline:

```powershell
$body = '{"source":"manual","ts":"2026-05-08T12:00:00Z","headline":"BTC breaks 80k"}'
Invoke-RestMethod -Method Post -Uri http://localhost:5000/newsfeed/push -Body $body -ContentType "application/json"
Invoke-RestMethod -Method Post -Uri http://localhost:5000/newsfeed/push -Body $body -ContentType "application/json"
```

Then check `dbo.AiInteractions`:

```sql
SELECT TOP 5 Purpose, InputHash, InputTokens, OutputTokens, CostUsd, CreatedAt
FROM dbo.AiInteractions
ORDER BY CreatedAt DESC;
```

Expected: exactly **one** row with `Purpose='SENTIMENT'` and the second push reuses it (within the 5-min TTL — see logs `AI cache hit purpose=SENTIMENT`).

## 5. Daily cap enforced (Definition of Done #3)

Set `Claude:DailyCapUsd` to something tiny in `appsettings.Development.json`:

```json
"Claude": { "DailyCapUsd": 0.01 }
```

Restart, push a couple of distinct headlines through the webhook, and watch
the logs. After the first call's recorded cost crosses the cap, the
analyzer logs:

```
WARN  AI daily cap reached purpose=SENTIMENT cap=0.01
WARN  Sentiment skipped: AI daily cap reached (0.0123/0.0100)
```

The bot keeps running on rule-based decisions (`IRegimeConfirmer` returns
the rule output, `ISetupConfirmer` returns `{approve=true, size_adj=0.7}`).

## 6. Sunday journal job (Definition of Done #4)

Trigger the Quartz job manually from a one-off REPL or extend the Worker
with a `/admin/run-journal` endpoint (not shipped). Easiest verification:

a. **Test path**: run `JournalCsvTests` — exercises `BuildCsv` + `IsoWeek`.

b. **Live path**: in the worker, set the cron to trigger soon (e.g. 5
   minutes from now) and tail the logs:

```json
"Journal": { "Cron": "0 35 12 ? * *" }   // change to a near-future minute
```

After the trigger:

```sql
SELECT TOP 1 * FROM dbo.AiJournals ORDER BY CreatedAt DESC;
```

Markdown file lands at `journals/{YYYY}-{WW}.md`. With zero closed trades the
job logs `no trades to analyze` and writes nothing — set `MaxTradesPerJournal=500`
and seed `dbo.TradeHistory` if you want a non-empty run.

## 7. Webhook end-to-end (n8n integration)

The endpoint is mapped at `POST /newsfeed/push`. Both shapes are accepted:

Single item:
```json
{ "source": "n8n", "ts": "2026-05-08T12:00:00Z", "headline": "BTC ETF approved" }
```

Batch:
```json
{
  "source": "n8n",
  "items": [
    { "ts": "2026-05-08T12:00:00Z", "headline": "BTC ETF approved" },
    { "ts": "2026-05-08T12:01:00Z", "headline": "ETH staking yields drop" }
  ]
}
```

If `News:WebhookSharedSecret` is set, send `X-Webhook-Secret: <value>` —
mismatches return 401.

## 8. RSS fallback

Populate `News:RssFeedUrls` with one or more feeds:

```json
"News": {
  "RssFeedUrls": [ "https://www.coindesk.com/arc/outboundfeeds/rss/" ]
}
```

The ingestion service polls every `News:PollInterval` (5m default) and the
analyzer dedupes by `(HeadlineHash, Asset)` natural key, so the RSS path can
run alongside CryptoPanic and the webhook without producing duplicates.

## Troubleshooting

| Symptom | Root cause | Fix |
|---------|------------|-----|
| `Required secret 'Anthropic:ApiKey' is not configured` on first call | API key missing | `dotnet user-secrets set Anthropic:ApiKey ...` |
| `401 Unauthorized` from Anthropic | Wrong/expired key | Reissue at console.anthropic.com |
| `429 Too Many Requests` | Outpaced the per-min limit upstream | Lower `Claude:RequestsPerMinute` |
| Duplicate sentiment rows for the same headline | Bug — natural key UNIQUE on `(HeadlineHash, Asset)` should prevent this | File a repro |
| Setup confirm always logs "fell back" | Anthropic > 2 s tail latency | Raise `SetupConfirmer:Timeout` or check network |
| Journal job never fires | Quartz hasn't been built/started | Check Worker logs for `JournalQuartzJob` registration |

## Files added in §S9

- `sql/migrations/008_news.sql`
- `src/TradingBot.AI/{Abstractions, Caching, Claude, Configuration, Cost, Journal, Models, Prompts, Regime, Sentiment, Setup, XgBoost}/*.cs`
- `src/TradingBot.AI/DependencyInjection/AiServiceCollectionExtensions.cs` (rewritten)
- `src/TradingBot.AI/TradingBot.AI.csproj` (Polly, Quartz, Syndication, AspNetCore framework reference)
- `src/TradingBot.Core/Domain/{NewsSentimentRecord, AiJournalRecord}.cs`
- `src/TradingBot.Data/Abstractions/{INewsSentimentRepository, IAiJournalRepository}.cs`
- `src/TradingBot.Data/Repositories/{NewsSentimentRepository, AiJournalRepository}.cs`
- `src/TradingBot.Data/Repositories/TradeHistoryRepository.cs` (added `GetInRangeAsync`)
- `src/TradingBot.Worker/Program.cs` (`AddAi(IConfiguration, ISecretsProvider)` overload + `MapNewsfeedPush()`)
- `src/TradingBot.Worker/appsettings.json` (Claude, News, RegimeConfirmer, SetupConfirmer, Journal sections)
- `tests/TradingBot.Tests/AI/*.cs`

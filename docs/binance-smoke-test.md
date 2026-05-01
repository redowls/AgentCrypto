# Binance Spot Testnet smoke test (S3)

End-to-end verification that S3 (Binance Integration) is wired correctly:
REST + WebSocket + reference data + watchdog + key safety.

## Prerequisites

1. Binance Spot Testnet account: <https://testnet.binance.vision/>.
   Generate an API key with **only** the `Enable Reading` and `Enable Spot &
   Margin Trading` permissions. **Do NOT enable withdrawals.** (The bot's
   `KeyPermissionVerifier` refuses to start if `WITHDRAW` is enabled.)
2. .NET 8 SDK installed.
3. Some testnet USDT on the account (the testnet faucet is on the account
   page).

## 1 — Set the testnet credentials in user-secrets

```powershell
cd src/TradingBot.Worker
dotnet user-secrets init
dotnet user-secrets set "Binance:ApiKey"    "<TESTNET_API_KEY>"
dotnet user-secrets set "Binance:ApiSecret" "<TESTNET_API_SECRET>"
dotnet user-secrets set "Binance:UseTestnet" "true"
```

The same values must NEVER be put in `appsettings.json` or environment files
checked into git — credentials flow only via user-secrets / Key Vault per
§9.7.

## 2 — Run the unit tests (no network required)

```bash
dotnet test --filter "FullyQualifiedName~TradingBot.Tests.Exchange&FullyQualifiedName!~Testnet"
```

Expected: filter clamping, kill-switch, exception classification, symbol
filter cache, and watchdog tests all pass. The live testnet tests are
auto-skipped because `BINANCE_TESTNET` is unset.

## 3 — Run the live testnet smoke tests

```bash
# bash / pwsh
$env:BINANCE_TESTNET="true"
$env:BINANCE_TESTNET_API_KEY="<TESTNET_API_KEY>"
$env:BINANCE_TESTNET_API_SECRET="<TESTNET_API_SECRET>"

dotnet test --filter "FullyQualifiedName~BinanceSpotTestnetSmokeTests"
```

Expected, in order:

- `ExchangeInfo_returns_active_symbols` — fetches `/exchangeInfo`, asserts
  > 50 symbols.
- `GetKlines_returns_recent_history` — fetches 100 × BTCUSDT 1-minute bars.
- `PlaceLimitOrder_far_from_market_then_cancel` — submits a 0.001 BTC LIMIT
  buy at half the last close, then cancels by `clientOrderId`. Status should
  go `NEW` → `CANCELED`.
- `SubscribeKline_receives_a_message_within_90s` — subscribes to BTCUSDT
  `kline_1m` and asserts at least one message arrives within 90 s.

If all four pass: REST, WebSocket, signing, and the resilience pipeline are
all functional against testnet.

## 4 — Run the bot host against testnet

```bash
cd src/TradingBot.Worker
dotnet run
```

Watch the console for:

1. `Binance key permission check passed.` — the key safety verifier.
2. `Reference data refresh for Spot: total=N inserted=N …` — exchangeInfo
   loaded and persisted to `dbo.Symbols`. **N must be ≥ 100** (testnet
   currently lists ~250+).
3. `Reference data refresh for UmFutures: …` — same for USDⓈ-M.
4. `Subscribed userData spot.userData (listenKey prefix=…)` once the
   execution engine starts.

DoD checks:

| Item | How to verify |
|------|---------------|
| Smoke test on Binance Spot Testnet places + cancels an order | Step 3, third test |
| WS receives kline updates for BTCUSDT for 5 minutes without disconnect | Run host (step 4) for 5+ min; check Serilog logs for any `Reconnecting` events on the spot kline stream — should be zero |
| Reference data refresh stores ≥100 symbols in DB | After step 4: `SELECT COUNT(*) FROM dbo.Symbols WHERE Exchange = 'BINANCE_SPOT'` should be ≥100 |
| Watchdog alert fires within 90s when WS is forcibly killed | See step 5 |

## 5 — Watchdog kill test

Pick one of:

- **Network drop**: while the host is running, disable your network adapter
  for 90 seconds. Within 60 s of the last received message you should see a
  log line like:
  ```
  CRIT WS WATCHDOG: stream spot.kline.BTCUSDT.1m stale (last event …, threshold 00:01:00).
  CRIT ALERT: WebSocket stream spot.kline.BTCUSDT.1m stale on Spot; …
  ```
- **Process the registry directly**: run the unit test
  `WebSocketWatchdogTests.Stale_stream_raises_alert_once` — proves the same
  code path with a faster threshold.

When the network comes back, Binance.Net's auto-reconnect kicks in
(exponential backoff, 2 s → 4 s → 8 s …) and the next received message
clears the stale flag in the watchdog. The next stall would fire a fresh
alert.

## 6 — Kill-switch / 418 handling

You cannot easily induce a real HTTP 418 on testnet. To exercise the path:

```csharp
var ks = serviceProvider.GetRequiredService<IBinanceKillSwitch>();
ks.Trip("manual test", DateTime.UtcNow.AddMinutes(5));
```

After this, every gateway call routed through the resilience pipeline throws
`BinanceKillSwitchTrippedException` immediately and the execution engine
must halt. Reset with `ks.Reset()`.

## 7 — Tear down

Cancel any open testnet orders on the Binance UI before terminating, so
listenKey expiry does not leak a parked GTC order.

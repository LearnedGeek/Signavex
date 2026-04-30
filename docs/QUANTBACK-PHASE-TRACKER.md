# Quantback — Phase Tracker

Portfolio-simulation backtest. Extends Signavex's point-in-time `/backtest` page into a 5-year mechanical-strategy simulator using the existing 18-signal scoring engine.

> **Origin:** prototype lives in [`tools/Quantback/`](../tools/Quantback/) (React/JSX, synthetic data, 4 indicators). It will not be ported as-is — Blazor mismatch, signal mismatch. The .NET implementation reuses Signavex's `ScanEngine` so the backtest reflects what the live picker actually does.

> **Why this exists:** answers "if you'd mechanically followed these signals for 5 years, what would your equity curve look like?" The current `/backtest` only answers "what would have surfaced on date X?" — point-in-time, not portfolio.

## Status legend
- [ ] not started
- [~] in progress
- [x] complete

---

## Q1 — Domain shapes & contracts ✅
- [x] **Q1.1** Records: `Position`, `Trade` (+ `TradeExitReason` enum), `EquityPoint`, `StrategyParameters`
- [x] **Q1.2** Request/result: `PortfolioBacktestRequest`, `PortfolioBacktestResult`, `PortfolioBacktestMetrics` (with `Empty` factories)
- [x] **Q1.3** `IPortfolioBacktester` interface in `Signavex.Domain.Interfaces`
- [x] **Q1.4** 7 shape tests pass: record equality, derived properties (Trade.HoldDays/ReturnPct), Empty round-trips request

**Exit criteria met:** types compile, no runtime logic, no DI yet. Pure shape work. All under `Signavex.Domain.Models.Portfolio` namespace to avoid colliding with the existing point-in-time `BacktestResult`.

---

## Q2 — Stub engine + DI wiring ✅
- [x] **Q2.1** `PortfolioBacktester` in `Signavex.Engine.Portfolio/` returns `PortfolioBacktestResult.Empty()` and logs the request
- [x] **Q2.2** Registered as `IPortfolioBacktester` in `AddSignavexEngine()`
- [x] **Q2.3** 3 tests: direct `RunAsync` echoes request + empty collections; DI resolves `IPortfolioBacktester → PortfolioBacktester`; cancellation token honored
- [x] **Q2.x** Added `Microsoft.Extensions.DependencyInjection` + `Microsoft.Extensions.Logging` to `Signavex.Engine.Tests` (needed for integration test)

**Exit criteria met:** `RunAsync(request)` resolves through DI and round-trips an empty result. Phase tracker visible in tracker file. Engine builds + ships.

---

## Q3 — Historical OHLCV pipeline ✅
- [x] **Q3.1** New `IHistoricalOhlcvProvider` interface in Domain — separate from `IMarketDataProvider` because cache characteristics are different (DB-persisted, no TTL for past dates)
- [x] **Q3.2** `PolygonHistoricalOhlcvProvider` uses `/v2/aggs` with explicit date range, `adjusted=true`, `limit=50000` (one call covers 5+ years per ticker)
- [x] **Q3.3** `HistoricalOhlcvEntity` + `AddHistoricalOhlcv` migration (table with unique index on `(Ticker, TradingDate)`, decimal(18,4) for prices)
- [x] **Q3.4** `DbCachedHistoricalOhlcvProvider` decorator — checks DB first, fetches on cold/stale (14-day coverage tolerance from latest cached date to requested `to`), upserts results
- [x] **Q3.5** DI wired: `IHistoricalOhlcvProvider → DbCachedHistoricalOhlcvProvider(PolygonHistoricalOhlcvProvider)`. Reuses existing `PolygonRateLimitingHandler` so the free-tier 5/min ceiling is enforced automatically.
- [x] **Q3.6** Tests: 4 Polygon (URL construction, bar parsing, inverted range, http error) + 6 cache (cold fetch+persist, warm hit, stale refetch with upsert dedup, inner-empty fallback, inverted range short-circuit, etc.)

**Exit criteria met:** can fetch 5 years of adjusted OHLCV for any ticker via DI. Cache survives worker restart (rows persist in `HistoricalOhlcv` table). Repeat backtests on same ticker hit DB only — zero Polygon calls until a stale-coverage trigger.

**Runtime cost on free tier (5 req/min):**
| Universe | Cold-cache time | Warm-cache time |
|---|---|---|
| 50 tickers | ~10 min | seconds |
| 100 tickers | ~20 min | seconds |
| S&P 500 only | ~100 min | seconds |
| S&P 500+400 (~900) | ~3 hours | seconds |

Cold-cache UX is addressed in Q6 (defer interactive run until cache is warm, or scope to smaller universe for first iteration).

---

## Q4 — Trade execution loop ✅
- [x] **Q4.1** `StrategyParameters` already shipped in Q1 (5/20/8/20 + signal-reversal flag + min-score)
- [x] **Q4.2** Day-by-day loop: pre-fetch OHLCV → derive trading days → for each day, score universe, process exits, open entries, snapshot equity
- [x] **Q4.3** Trade log captures entry/exit dates, prices, exit reason, realized P&L; force-closes remaining positions at last day with `EndOfBacktest` reason
- [x] **Q4.4** Composes `IEnumerable<IStockSignal>` + `ScoreCalculator` directly (same primitives `StockEvaluator` uses) — no scoring duplication
- [x] **Q4.5** 9 tests cover: empty universe, threshold-gated entries, position sizing, stop-loss intraday low, take-profit intraday high, signal-reversal exit, end-of-backtest cleanup, basic metrics counts, DI resolution
- [x] **Q4.6** Same-day re-entry guard: a ticker exited today is skipped for entry that same day (avoids whipsaw — if signal still says buy tomorrow, we re-enter then)

**Caveats (deliberate scope limits):**
- Scoring uses live `IStockSignal` set against per-day-trimmed OHLCV. Fundamentals + sentiment + market signals receive null/empty inputs and self-report unavailable, so the score is technical-only. Historical fundamentals + news replay is out of scope until that data is available.
- No market-context multiplier applied. Historical macro replay isn't wired up.
- Stop-loss wins ties when both stop and target trigger on the same day (conservative).

**Exit criteria met:** backtest produces non-empty trade log + equity curve for small ticker sets in tests. Deterministic — same inputs produce same outputs.

---

## Q5 — Metrics
- [ ] **Q5.1** Equity curve as time series
- [ ] **Q5.2** Total return, annualized return
- [ ] **Q5.3** Sharpe ratio (annualized, vs risk-free rate from FRED)
- [ ] **Q5.4** Max drawdown (peak-to-trough %)
- [ ] **Q5.5** Win rate, avg win/loss, avg hold days
- [ ] **Q5.6** Monthly P&L breakdown
- [ ] **Q5.7** Per-ticker breakdown
- [ ] **Q5.8** Tests against Quantback's React prototype outputs to validate math

**Exit criteria:** metrics match a hand-verified spreadsheet for a 1-year canned scenario.

---

## Q6 — UI
- [ ] **Q6.1** New page `/quantback` (Pro-gated; mirror existing `/backtest` auth pattern)
- [ ] **Q6.2** Config form: date range, universe slice, strategy params
- [ ] **Q6.3** Equity curve chart (lightweight-charts, line series)
- [ ] **Q6.4** Metrics summary cards
- [ ] **Q6.5** Trade log table (paginated, sortable)
- [ ] **Q6.6** Per-ticker breakdown table
- [ ] **Q6.7** "Save scenario" so users can re-run with same parameters

**Exit criteria:** user can configure + run a backtest, see all metrics + chart + trade log on one page.

---

## Q7 — Realism polish (optional, post-launch)
- [ ] **Q7.1** Slippage model (bps per side, configurable)
- [ ] **Q7.2** Commissions (flat or per-share, configurable)
- [ ] **Q7.3** Survivorship bias note: source historical delisted tickers if available
- [ ] **Q7.4** Corporate actions: confirm `adjusted=true` covers splits/divs adequately
- [ ] **Q7.5** Cash drag (idle cash earns short-term Treasury rate)

---

## Notes
- Phases Q1-Q2 are pure code, no external dependency. Ship in one sitting.
- Q3 is the data dependency. Polygon free-tier rate limits matter — may force background pre-warming.
- Q4 is where the fundamental signals shipped on 2026-04-30 actually pay off — they're inputs to the day-by-day scoring.
- Q5 metrics should be validated against the Quantback React prototype for sanity.
- Q6 UI is the user-visible surface. Defer until Q5 numbers are trustworthy.

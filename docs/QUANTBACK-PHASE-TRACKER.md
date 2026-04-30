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

## Q3 — Historical OHLCV pipeline
- [ ] **Q3.1** Extend `PolygonMarketDataProvider` for multi-year ranges (paged if needed)
- [ ] **Q3.2** Verify adjusted-close behavior — Polygon's `/v2/aggs` `adjusted=true` flag
- [ ] **Q3.3** Add a separate cache lane for historical data (longer TTL than 15min, larger key space)
- [ ] **Q3.4** Bulk-fetch helper: given universe + date range, return per-ticker OHLCV
- [ ] **Q3.5** Cost analysis: free tier 5 req/min → estimate scan time for 5y × 900 tickers

**Exit criteria:** can fetch 5 years of adjusted OHLCV for an arbitrary ticker. Cache survives a worker restart. Documented runtime cost for full universe.

---

## Q4 — Trade execution loop
- [ ] **Q4.1** Strategy parameters: position size %, max per-ticker %, stop-loss %, take-profit %, signal-reversal exit
- [ ] **Q4.2** Per-day simulation step: score universe, open/close positions per rules, update equity
- [ ] **Q4.3** Trade log capture: entry/exit dates, prices, P&L per trade
- [ ] **Q4.4** Reuse `ScanEngine` for daily scoring — no parallel scoring code path
- [ ] **Q4.5** Tests with synthetic OHLCV + canned scores to verify entry/exit/sizing rules

**Exit criteria:** backtest produces a non-empty trade log + equity curve for a small ticker set. Numbers reproducible across runs (deterministic).

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

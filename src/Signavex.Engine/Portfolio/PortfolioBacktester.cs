using Microsoft.Extensions.Logging;
using Signavex.Domain.Interfaces;
using Signavex.Domain.Models;
using Signavex.Domain.Models.Portfolio;

namespace Signavex.Engine.Portfolio;

/// <summary>
/// Day-by-day mechanical simulation against historical OHLCV.
///
/// Caveats (will tighten in later phases):
/// - Scoring uses the live <see cref="IStockSignal"/> set against per-day-trimmed
///   OHLCV. Fundamental and sentiment signals receive null/empty inputs and so
///   self-report unavailable; the score for each day is therefore driven by the
///   technical signals (RSI, MACD, MAs, channel, support/resistance, etc.).
/// - No market-context multiplier is applied — historical macro replay isn't
///   in scope until economic time-series are wired up.
/// - Stop-loss and take-profit triggers use the day's high/low. If both
///   triggered intra-day, stop-loss wins (conservative).
/// </summary>
public class PortfolioBacktester : IPortfolioBacktester
{
    private readonly IHistoricalOhlcvProvider _historical;
    private readonly IEnumerable<IStockSignal> _signals;
    private readonly ScoreCalculator _scoreCalculator;
    private readonly ILogger<PortfolioBacktester> _logger;

    public PortfolioBacktester(
        IHistoricalOhlcvProvider historical,
        IEnumerable<IStockSignal> signals,
        ScoreCalculator scoreCalculator,
        ILogger<PortfolioBacktester> logger)
    {
        _historical = historical;
        _signals = signals;
        _scoreCalculator = scoreCalculator;
        _logger = logger;
    }

    public async Task<PortfolioBacktestResult> RunAsync(PortfolioBacktestRequest request, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;

        var bars = await PrefetchUniverseAsync(request, ct);
        if (bars.Count == 0)
        {
            _logger.LogWarning("Quantback: no OHLCV returned for any ticker in universe — returning empty result.");
            return PortfolioBacktestResult.Empty(request, startedAt);
        }

        var tradingDays = bars.Values
            .SelectMany(b => b.Select(r => r.Date))
            .Where(d => d >= request.StartDate && d <= request.EndDate)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        if (tradingDays.Count == 0)
            return PortfolioBacktestResult.Empty(request, startedAt);

        var state = new PortfolioState(request.StartingCapital);
        var trades = new List<Trade>();
        var equityCurve = new List<EquityPoint>(tradingDays.Count);

        DateOnly? prevDay = null;
        foreach (var day in tradingDays)
        {
            ct.ThrowIfCancellationRequested();

            // Cash drag — idle cash earns the risk-free rate over the calendar
            // gap since yesterday. Q7.5. Skip the first day (no gap to accrue).
            if (prevDay is DateOnly p && request.Strategy.RiskFreeAnnualRate > 0 && state.Cash > 0)
            {
                var calDays = (day.ToDateTime(TimeOnly.MinValue) - p.ToDateTime(TimeOnly.MinValue)).TotalDays;
                if (calDays > 0)
                {
                    var dailyRate = (decimal)(request.Strategy.RiskFreeAnnualRate / 365.0);
                    state.Cash *= (1m + dailyRate * (decimal)calDays);
                }
            }
            prevDay = day;

            state.ExitedToday.Clear();
            var scores = await ScoreUniverseAsync(bars, day);
            ProcessExits(state, bars, day, scores, request.Strategy, trades);
            OpenEntries(state, bars, day, scores, request.Strategy);
            equityCurve.Add(SnapshotEquity(state, bars, day));
        }

        // End-of-backtest: close any remaining positions at the last available
        // close so equity captures the unrealized P&L instead of leaving it
        // floating.
        var lastDay = tradingDays[^1];
        ForceCloseAll(state, bars, lastDay, request.Strategy, trades);
        if (equityCurve.Count > 0)
            equityCurve[^1] = SnapshotEquity(state, bars, lastDay);

        var metrics = PortfolioMetricsCalculator.ComputeMetrics(request.StartingCapital, trades, equityCurve);
        var monthlyPnL = PortfolioMetricsCalculator.ComputeMonthlyPnL(trades);
        var perTicker = PortfolioMetricsCalculator.ComputePerTickerBreakdown(trades);

        _logger.LogInformation(
            "Quantback complete: {TradingDays} days, {TradeCount} trades, end equity ${EndingEquity:F2}, total return {TotalReturn:P2}, Sharpe {Sharpe:F2}, max DD {MaxDD:P2}",
            tradingDays.Count, trades.Count, metrics.EndingEquity, metrics.TotalReturnPct, metrics.SharpeRatio, metrics.MaxDrawdownPct);

        return new PortfolioBacktestResult(
            request,
            equityCurve,
            trades,
            state.OpenPositions.Values.ToList(),
            metrics,
            monthlyPnL,
            perTicker,
            startedAt,
            DateTime.UtcNow);
    }

    private async Task<Dictionary<string, IReadOnlyList<OhlcvRecord>>> PrefetchUniverseAsync(
        PortfolioBacktestRequest request,
        CancellationToken ct)
    {
        var bars = new Dictionary<string, IReadOnlyList<OhlcvRecord>>(request.Universe.Count);
        foreach (var ticker in request.Universe)
        {
            ct.ThrowIfCancellationRequested();
            var rows = await _historical.GetHistoricalDailyOhlcvAsync(ticker, request.StartDate, request.EndDate, ct);
            if (rows.Count > 0)
                bars[ticker] = rows;
            else
                _logger.LogDebug("Quantback: no OHLCV for {Ticker} {From}–{To} — excluding from universe", ticker, request.StartDate, request.EndDate);
        }
        return bars;
    }

    private async Task<Dictionary<string, double>> ScoreUniverseAsync(
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvRecord>> bars,
        DateOnly day)
    {
        var scores = new Dictionary<string, double>(bars.Count);
        foreach (var (ticker, history) in bars)
        {
            var trimmed = TrimTo(history, day);
            if (trimmed.Count == 0) continue;
            var stockData = new StockData(ticker, ticker, trimmed, null, Array.Empty<NewsItem>());
            var signalTasks = _signals.Select(s => s.EvaluateAsync(stockData));
            var results = await Task.WhenAll(signalTasks);
            scores[ticker] = _scoreCalculator.CalculateWeightedScore(results);
        }
        return scores;
    }

    private static void ProcessExits(
        PortfolioState state,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvRecord>> bars,
        DateOnly day,
        IReadOnlyDictionary<string, double> scores,
        StrategyParameters strategy,
        List<Trade> trades)
    {
        var toClose = new List<(string Ticker, decimal RawExitPrice, TradeExitReason Reason)>();

        foreach (var (ticker, position) in state.OpenPositions)
        {
            if (!TryGetBar(bars[ticker], day, out var bar))
                continue;

            // Stop-loss wins ties — conservative.
            if (bar.Low <= position.StopLossPrice)
            {
                toClose.Add((ticker, position.StopLossPrice, TradeExitReason.StopLoss));
                continue;
            }

            if (bar.High >= position.TakeProfitPrice)
            {
                toClose.Add((ticker, position.TakeProfitPrice, TradeExitReason.TakeProfit));
                continue;
            }

            if (strategy.ExitOnSignalReversal &&
                scores.TryGetValue(ticker, out var todayScore) &&
                todayScore < strategy.MinScoreToEnter)
            {
                toClose.Add((ticker, bar.Close, TradeExitReason.SignalReversal));
            }
        }

        foreach (var (ticker, rawExit, reason) in toClose)
        {
            var pos = state.OpenPositions[ticker];
            // Slippage on exit — sellers receive slightly less.
            var exitPrice = ApplySlippage(rawExit, strategy.SlippageBps, isBuy: false);
            var grossProceeds = pos.Shares * exitPrice;
            var realized = (exitPrice - pos.EntryPrice) * pos.Shares - strategy.CommissionPerTrade;

            trades.Add(new Trade(
                Ticker: ticker,
                Shares: pos.Shares,
                EntryDate: pos.EntryDate,
                EntryPrice: pos.EntryPrice,
                ExitDate: day,
                ExitPrice: exitPrice,
                ExitReason: reason,
                RealizedPnL: realized));
            state.Cash += grossProceeds - strategy.CommissionPerTrade;
            state.OpenPositions.Remove(ticker);
            state.ExitedToday.Add(ticker);
        }
    }

    private void OpenEntries(
        PortfolioState state,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvRecord>> bars,
        DateOnly day,
        IReadOnlyDictionary<string, double> scores,
        StrategyParameters strategy)
    {
        var totalEquity = state.Cash + state.GetPositionsValue(bars, day);
        var perTickerCap = totalEquity * strategy.MaxPerTickerPct;

        // Score-descending so the strongest candidates get cash first when
        // budget is tight. Filter out:
        //   - tickers already held (no doubling up)
        //   - tickers we just exited on this same day (avoids whipsaw — a
        //     stop-out followed by an immediate re-entry isn't a strategy
        //     real money would run; if the signal still says "buy" tomorrow,
        //     we'll re-enter then)
        var candidates = scores
            .Where(kvp =>
                kvp.Value >= strategy.MinScoreToEnter &&
                !state.OpenPositions.ContainsKey(kvp.Key) &&
                !state.ExitedToday.Contains(kvp.Key))
            .OrderByDescending(kvp => kvp.Value);

        foreach (var (ticker, score) in candidates)
        {
            if (!TryGetBar(bars[ticker], day, out var bar))
                continue;
            if (bar.Close <= 0)
                continue;

            // Slippage on entry — buy at a slightly worse (higher) price.
            var entryPrice = ApplySlippage(bar.Close, strategy.SlippageBps, isBuy: true);

            var budget = Math.Min(state.Cash, Math.Min(totalEquity * strategy.PositionSizePct, perTickerCap));
            // Commission consumes some of the budget up front; ensure shares
            // can be afforded after both cost and commission.
            var availableForShares = budget - strategy.CommissionPerTrade;
            if (availableForShares <= 0) continue;

            var shares = (int)(availableForShares / entryPrice);
            if (shares <= 0)
                continue;

            var cost = shares * entryPrice + strategy.CommissionPerTrade;
            if (cost > state.Cash) continue;

            state.Cash -= cost;
            state.OpenPositions[ticker] = new Position(
                Ticker: ticker,
                Shares: shares,
                EntryPrice: entryPrice,
                EntryDate: day,
                EntryReason: $"Score {score:F2} ≥ {strategy.MinScoreToEnter:F2}",
                StopLossPrice: entryPrice * (1 - strategy.StopLossPct),
                TakeProfitPrice: entryPrice * (1 + strategy.TakeProfitPct));
        }
    }

    /// <summary>
    /// Adjust price by <paramref name="slippageBps"/> basis points. Buyers
    /// pay slightly more, sellers receive slightly less. 1 bp = 0.01%.
    /// </summary>
    public static decimal ApplySlippage(decimal price, double slippageBps, bool isBuy)
    {
        if (slippageBps <= 0) return price;
        var factor = (decimal)(slippageBps / 10_000.0);
        return isBuy ? price * (1m + factor) : price * (1m - factor);
    }

    private static EquityPoint SnapshotEquity(
        PortfolioState state,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvRecord>> bars,
        DateOnly day)
    {
        var positionsValue = state.GetPositionsValue(bars, day);
        return new EquityPoint(
            Date: day,
            Cash: state.Cash,
            PositionsValue: positionsValue,
            TotalEquity: state.Cash + positionsValue,
            OpenPositionCount: state.OpenPositions.Count);
    }

    private static void ForceCloseAll(
        PortfolioState state,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvRecord>> bars,
        DateOnly lastDay,
        StrategyParameters strategy,
        List<Trade> trades)
    {
        foreach (var (ticker, position) in state.OpenPositions.ToList())
        {
            if (!TryGetBar(bars[ticker], lastDay, out var bar))
                continue;

            var exitPrice = ApplySlippage(bar.Close, strategy.SlippageBps, isBuy: false);
            var realized = (exitPrice - position.EntryPrice) * position.Shares - strategy.CommissionPerTrade;
            trades.Add(new Trade(
                Ticker: ticker,
                Shares: position.Shares,
                EntryDate: position.EntryDate,
                EntryPrice: position.EntryPrice,
                ExitDate: lastDay,
                ExitPrice: exitPrice,
                ExitReason: TradeExitReason.EndOfBacktest,
                RealizedPnL: realized));
            state.Cash += position.Shares * exitPrice - strategy.CommissionPerTrade;
            state.OpenPositions.Remove(ticker);
        }
    }

    private static IReadOnlyList<OhlcvRecord> TrimTo(IReadOnlyList<OhlcvRecord> history, DateOnly day) =>
        history.Where(r => r.Date <= day).ToList();

    private static bool TryGetBar(IReadOnlyList<OhlcvRecord> bars, DateOnly day, out OhlcvRecord bar)
    {
        // Bars are sorted ascending by Date out of the historical provider.
        for (int i = bars.Count - 1; i >= 0; i--)
        {
            if (bars[i].Date == day)
            {
                bar = bars[i];
                return true;
            }
            if (bars[i].Date < day)
                break;
        }
        bar = default!;
        return false;
    }

    /// <summary>Mutable simulation state. Owned by a single <see cref="RunAsync"/> call.</summary>
    private sealed class PortfolioState
    {
        public decimal Cash;
        public Dictionary<string, Position> OpenPositions { get; } = new();
        public HashSet<string> ExitedToday { get; } = new();

        public PortfolioState(decimal startingCapital) => Cash = startingCapital;

        public decimal GetPositionsValue(
            IReadOnlyDictionary<string, IReadOnlyList<OhlcvRecord>> bars,
            DateOnly day)
        {
            decimal total = 0;
            foreach (var (ticker, pos) in OpenPositions)
            {
                if (bars.TryGetValue(ticker, out var history) && TryGetBar(history, day, out var bar))
                    total += pos.Shares * bar.Close;
                else
                    total += pos.Shares * pos.EntryPrice;  // Best estimate when no bar today
            }
            return total;
        }
    }
}

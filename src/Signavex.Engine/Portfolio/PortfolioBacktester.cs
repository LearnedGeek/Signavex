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

        foreach (var day in tradingDays)
        {
            ct.ThrowIfCancellationRequested();

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
        ForceCloseAll(state, bars, lastDay, trades);
        if (equityCurve.Count > 0)
            equityCurve[^1] = SnapshotEquity(state, bars, lastDay);

        var metrics = ComputeBasicMetrics(request, trades, equityCurve);

        _logger.LogInformation(
            "Quantback complete: {TradingDays} days, {Entries} entries, {Exits} exits, end equity ${EndingEquity:F2}",
            tradingDays.Count, trades.Count, trades.Count, metrics.EndingEquity);

        return new PortfolioBacktestResult(
            request,
            equityCurve,
            trades,
            state.OpenPositions.Values.ToList(),
            metrics,
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
        var toClose = new List<(string Ticker, decimal ExitPrice, TradeExitReason Reason)>();

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

        foreach (var (ticker, exitPrice, reason) in toClose)
        {
            var pos = state.OpenPositions[ticker];
            var realized = (exitPrice - pos.EntryPrice) * pos.Shares;
            trades.Add(new Trade(
                Ticker: ticker,
                Shares: pos.Shares,
                EntryDate: pos.EntryDate,
                EntryPrice: pos.EntryPrice,
                ExitDate: day,
                ExitPrice: exitPrice,
                ExitReason: reason,
                RealizedPnL: realized));
            state.Cash += pos.Shares * exitPrice;
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

            var budget = Math.Min(state.Cash, Math.Min(totalEquity * strategy.PositionSizePct, perTickerCap));
            var shares = (int)(budget / bar.Close);
            if (shares <= 0)
                continue;

            var cost = shares * bar.Close;
            state.Cash -= cost;
            state.OpenPositions[ticker] = new Position(
                Ticker: ticker,
                Shares: shares,
                EntryPrice: bar.Close,
                EntryDate: day,
                EntryReason: $"Score {score:F2} ≥ {strategy.MinScoreToEnter:F2}",
                StopLossPrice: bar.Close * (1 - strategy.StopLossPct),
                TakeProfitPrice: bar.Close * (1 + strategy.TakeProfitPct));
        }
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
        List<Trade> trades)
    {
        foreach (var (ticker, position) in state.OpenPositions.ToList())
        {
            if (!TryGetBar(bars[ticker], lastDay, out var bar))
                continue;

            var realized = (bar.Close - position.EntryPrice) * position.Shares;
            trades.Add(new Trade(
                Ticker: ticker,
                Shares: position.Shares,
                EntryDate: position.EntryDate,
                EntryPrice: position.EntryPrice,
                ExitDate: lastDay,
                ExitPrice: bar.Close,
                ExitReason: TradeExitReason.EndOfBacktest,
                RealizedPnL: realized));
            state.Cash += position.Shares * bar.Close;
            state.OpenPositions.Remove(ticker);
        }
    }

    private static PortfolioBacktestMetrics ComputeBasicMetrics(
        PortfolioBacktestRequest request,
        IReadOnlyList<Trade> trades,
        IReadOnlyList<EquityPoint> equityCurve)
    {
        var endingEquity = equityCurve.Count > 0 ? equityCurve[^1].TotalEquity : request.StartingCapital;
        var totalReturn = request.StartingCapital == 0 ? 0.0 : (double)((endingEquity - request.StartingCapital) / request.StartingCapital);

        var winners = trades.Where(t => t.RealizedPnL > 0).ToList();
        var losers = trades.Where(t => t.RealizedPnL < 0).ToList();
        var avgHoldDays = trades.Count == 0 ? 0.0 : trades.Average(t => (double)t.HoldDays);
        var winRate = trades.Count == 0 ? 0.0 : (double)winners.Count / trades.Count;

        return new PortfolioBacktestMetrics(
            StartingEquity: request.StartingCapital,
            EndingEquity: endingEquity,
            TotalReturnPct: totalReturn,
            AnnualizedReturnPct: 0.0,        // Q5
            SharpeRatio: 0.0,                // Q5
            MaxDrawdownPct: 0.0,             // Q5
            TotalTrades: trades.Count,
            WinningTrades: winners.Count,
            LosingTrades: losers.Count,
            WinRate: winRate,
            AvgWinPnL: winners.Count == 0 ? 0m : winners.Average(t => t.RealizedPnL),
            AvgLossPnL: losers.Count == 0 ? 0m : losers.Average(t => t.RealizedPnL),
            AvgHoldDays: avgHoldDays);
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

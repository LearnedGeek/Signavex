using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Signavex.Domain.Interfaces;
using Signavex.Domain.Models;
using Signavex.Domain.Models.Portfolio;
using Signavex.Engine.Portfolio;

namespace Signavex.Engine.Tests.Portfolio;

/// <summary>
/// Q4: day-by-day simulation. Uses a fake historical-OHLCV provider plus a
/// scripted IStockSignal so each scenario exercises one rule (entry, stop,
/// target, signal-reversal, end-of-backtest cleanup) in isolation.
/// </summary>
public class PortfolioBacktesterTests
{
    private static PortfolioBacktestRequest Request(
        StrategyParameters? strategy = null,
        params string[] universe) => new(
            StartDate: new DateOnly(2024, 1, 1),
            EndDate: new DateOnly(2024, 1, 10),
            StartingCapital: 100_000m,
            Universe: universe.Length == 0 ? new[] { "AAPL" } : universe,
            Strategy: strategy ?? StrategyParameters.Default);

    private static PortfolioBacktester MakeBacktester(
        FakeHistoricalProvider history,
        Func<StockData, double> scoreOf)
    {
        var signal = new ScriptedSignal(scoreOf);
        return new PortfolioBacktester(
            history,
            new IStockSignal[] { signal },
            new ScoreCalculator(),
            NullLogger<PortfolioBacktester>.Instance);
    }

    [Fact]
    public async Task EmptyUniverse_ReturnsEmpty()
    {
        var history = new FakeHistoricalProvider();
        var backtester = MakeBacktester(history, _ => 0);

        var request = new PortfolioBacktestRequest(
            new DateOnly(2024, 1, 1),
            new DateOnly(2024, 1, 5),
            100_000m,
            Array.Empty<string>(),
            StrategyParameters.Default);

        var result = await backtester.RunAsync(request);
        Assert.Empty(result.EquityCurve);
        Assert.Empty(result.Trades);
    }

    [Fact]
    public async Task ScoresBelowThreshold_NoTradesOpened()
    {
        var history = new FakeHistoricalProvider();
        history.Add("AAPL", FlatBars("AAPL", new DateOnly(2024, 1, 1), 5, price: 100m));

        var backtester = MakeBacktester(history, _ => 0.0);  // Always neutral, below 0.45

        var result = await backtester.RunAsync(Request(universe: "AAPL"));

        Assert.Empty(result.Trades);
        Assert.Empty(result.OpenPositions);
        // Cash unchanged since no trades.
        Assert.Equal(100_000m, result.EquityCurve[^1].Cash);
    }

    [Fact]
    public async Task ScoreAboveThreshold_OpensPosition_WithCorrectSizing()
    {
        var history = new FakeHistoricalProvider();
        history.Add("AAPL", FlatBars("AAPL", new DateOnly(2024, 1, 1), 5, price: 100m));

        var strategy = StrategyParameters.Default with
        {
            ExitOnSignalReversal = false,    // Keep the position open the whole run
            StopLossPct = 0.5m,              // Far enough away the stop won't fire
            TakeProfitPct = 0.5m,            // Far enough away the target won't fire
        };

        var backtester = MakeBacktester(history, _ => 0.9);  // Always strong buy

        var result = await backtester.RunAsync(Request(strategy, "AAPL"));

        // Position should remain open all the way to end-of-backtest, which
        // closes it. So we expect exactly one trade with reason EndOfBacktest.
        Assert.Single(result.Trades);
        var trade = result.Trades[0];
        Assert.Equal(TradeExitReason.EndOfBacktest, trade.ExitReason);

        // Sizing: PositionSizePct = 5% of $100k = $5,000 → 50 shares at $100 = $5,000.
        Assert.Equal(50, trade.Shares);
        Assert.Equal(100m, trade.EntryPrice);
    }

    [Fact]
    public async Task StopLoss_TriggersOnIntradayLow()
    {
        var history = new FakeHistoricalProvider();
        // Day 1: enter at $100 (stop-loss at $92 = 100 × (1 - 0.08))
        // Day 2: low dips to $91 → stop should fire at $92
        history.Add("AAPL", new[]
        {
            Bar("AAPL", new DateOnly(2024, 1, 1), open: 100, high: 101, low: 99, close: 100),
            Bar("AAPL", new DateOnly(2024, 1, 2), open: 95, high: 95, low: 91, close: 93),
        });

        var backtester = MakeBacktester(history, _ => 0.9);
        var result = await backtester.RunAsync(Request(universe: "AAPL") with { EndDate = new DateOnly(2024, 1, 2) });

        Assert.Single(result.Trades);
        var trade = result.Trades[0];
        Assert.Equal(TradeExitReason.StopLoss, trade.ExitReason);
        Assert.Equal(92m, trade.ExitPrice);  // 100 × (1 - 0.08)
    }

    [Fact]
    public async Task TakeProfit_TriggersOnIntradayHigh()
    {
        var history = new FakeHistoricalProvider();
        // Day 1: enter at $100, target $120
        // Day 2: high reaches $125 → target fires at $120
        history.Add("AAPL", new[]
        {
            Bar("AAPL", new DateOnly(2024, 1, 1), open: 100, high: 101, low: 99, close: 100),
            Bar("AAPL", new DateOnly(2024, 1, 2), open: 105, high: 125, low: 104, close: 122),
        });

        var backtester = MakeBacktester(history, _ => 0.9);
        var result = await backtester.RunAsync(Request(universe: "AAPL") with { EndDate = new DateOnly(2024, 1, 2) });

        Assert.Single(result.Trades);
        var trade = result.Trades[0];
        Assert.Equal(TradeExitReason.TakeProfit, trade.ExitReason);
        Assert.Equal(120m, trade.ExitPrice);  // 100 × (1 + 0.20)
    }

    [Fact]
    public async Task SignalReversalExit_ClosesAtClose()
    {
        var history = new FakeHistoricalProvider();
        history.Add("AAPL", new[]
        {
            Bar("AAPL", new DateOnly(2024, 1, 1), open: 100, high: 101, low: 99, close: 100),
            // Day 2: small price move (no stop, no target) but score will flip negative
            Bar("AAPL", new DateOnly(2024, 1, 2), open: 100, high: 102, low: 98, close: 99),
        });

        // Score positive day 1 (so we enter), negative day 2 (so we exit).
        var backtester = MakeBacktester(history, sd =>
            sd.OhlcvHistory.Last().Date == new DateOnly(2024, 1, 1) ? 0.9 : -0.5);

        var result = await backtester.RunAsync(Request(universe: "AAPL") with { EndDate = new DateOnly(2024, 1, 2) });

        Assert.Single(result.Trades);
        var trade = result.Trades[0];
        Assert.Equal(TradeExitReason.SignalReversal, trade.ExitReason);
        Assert.Equal(99m, trade.ExitPrice);  // Day 2 close
    }

    [Fact]
    public async Task EndOfBacktest_ForcesCloseRemainingPositions()
    {
        var history = new FakeHistoricalProvider();
        history.Add("AAPL", FlatBars("AAPL", new DateOnly(2024, 1, 1), 3, price: 100m));

        // Stays profitable, no exits, stable price.
        var strategy = StrategyParameters.Default with
        {
            ExitOnSignalReversal = false,
            StopLossPct = 0.5m,
            TakeProfitPct = 0.5m,
        };
        var backtester = MakeBacktester(history, _ => 0.9);

        var result = await backtester.RunAsync(Request(strategy, "AAPL") with { EndDate = new DateOnly(2024, 1, 3) });

        Assert.Single(result.Trades);
        Assert.Equal(TradeExitReason.EndOfBacktest, result.Trades[0].ExitReason);
        Assert.Empty(result.OpenPositions);
    }

    [Fact]
    public async Task Metrics_BasicCountsArePopulated()
    {
        var history = new FakeHistoricalProvider();
        history.Add("AAPL", new[]
        {
            Bar("AAPL", new DateOnly(2024, 1, 1), open: 100, high: 101, low: 99, close: 100),
            Bar("AAPL", new DateOnly(2024, 1, 2), open: 105, high: 125, low: 104, close: 122),  // hits target
        });
        var backtester = MakeBacktester(history, _ => 0.9);

        var result = await backtester.RunAsync(Request(universe: "AAPL") with { EndDate = new DateOnly(2024, 1, 2) });

        Assert.Equal(1, result.Metrics.TotalTrades);
        Assert.Equal(1, result.Metrics.WinningTrades);
        Assert.Equal(0, result.Metrics.LosingTrades);
        Assert.Equal(1.0, result.Metrics.WinRate);
        Assert.True(result.Metrics.EndingEquity > 100_000m);
        Assert.True(result.Metrics.TotalReturnPct > 0);
    }

    [Fact]
    public async Task DiContainer_ResolvesIPortfolioBacktester()
    {
        // The full real registration. We don't run a backtest here, just confirm
        // resolution succeeds — Q3's IHistoricalOhlcvProvider isn't part of
        // AddSignavexEngine, so a real RunAsync would need it provided too.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHistoricalOhlcvProvider>(new FakeHistoricalProvider());
        services.AddSignavexEngine();
        // Engine needs the signal collection — use empty for resolve check.
        services.AddSingleton<IEnumerable<IStockSignal>>(Array.Empty<IStockSignal>());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var backtester = scope.ServiceProvider.GetRequiredService<IPortfolioBacktester>();
        Assert.IsType<PortfolioBacktester>(backtester);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static OhlcvRecord Bar(string ticker, DateOnly date, decimal open, decimal high, decimal low, decimal close, long volume = 1_000_000) =>
        new(ticker, date, open, high, low, close, volume);

    private static List<OhlcvRecord> FlatBars(string ticker, DateOnly start, int days, decimal price)
    {
        var list = new List<OhlcvRecord>();
        for (int i = 0; i < days; i++)
            list.Add(Bar(ticker, start.AddDays(i), price, price, price, price));
        return list;
    }

    private sealed class FakeHistoricalProvider : IHistoricalOhlcvProvider
    {
        private readonly Dictionary<string, IReadOnlyList<OhlcvRecord>> _store = new();

        public void Add(string ticker, IEnumerable<OhlcvRecord> bars) =>
            _store[ticker] = bars.OrderBy(b => b.Date).ToList();

        public Task<IReadOnlyList<OhlcvRecord>> GetHistoricalDailyOhlcvAsync(
            string ticker, DateOnly from, DateOnly to, CancellationToken ct = default)
        {
            if (!_store.TryGetValue(ticker, out var rows))
                return Task.FromResult<IReadOnlyList<OhlcvRecord>>(Array.Empty<OhlcvRecord>());
            return Task.FromResult<IReadOnlyList<OhlcvRecord>>(
                rows.Where(r => r.Date >= from && r.Date <= to).ToList());
        }
    }

    private sealed class ScriptedSignal : IStockSignal
    {
        private readonly Func<StockData, double> _scoreOf;

        public ScriptedSignal(Func<StockData, double> scoreOf) => _scoreOf = scoreOf;

        public string Name => "ScriptedSignal";
        public double DefaultWeight => 1.0;

        public Task<SignalResult> EvaluateAsync(StockData stock) =>
            Task.FromResult(new SignalResult(Name, _scoreOf(stock), DefaultWeight, "scripted", true));
    }
}

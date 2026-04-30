using Microsoft.Extensions.Logging.Abstractions;
using Signavex.Domain.Interfaces;
using Signavex.Domain.Models;
using Signavex.Domain.Models.Portfolio;
using Signavex.Engine.Portfolio;

namespace Signavex.Engine.Tests.Portfolio;

/// <summary>
/// Q7: realism polish — slippage, commissions, cash drag. Each rule
/// is opt-in via <see cref="StrategyParameters"/>, so legacy callers
/// see no change unless they pass non-zero values.
/// </summary>
public class PortfolioBacktesterRealismTests
{
    private static OhlcvRecord Bar(string ticker, DateOnly date, decimal open, decimal high, decimal low, decimal close) =>
        new(ticker, date, open, high, low, close, 1_000_000);

    private sealed class FakeHist : IHistoricalOhlcvProvider
    {
        private readonly Dictionary<string, IReadOnlyList<OhlcvRecord>> _store = new();
        public void Add(string ticker, IEnumerable<OhlcvRecord> bars) =>
            _store[ticker] = bars.OrderBy(b => b.Date).ToList();
        public Task<IReadOnlyList<OhlcvRecord>> GetHistoricalDailyOhlcvAsync(string ticker, DateOnly from, DateOnly to, CancellationToken ct = default) =>
            _store.TryGetValue(ticker, out var rows)
                ? Task.FromResult<IReadOnlyList<OhlcvRecord>>(rows.Where(r => r.Date >= from && r.Date <= to).ToList())
                : Task.FromResult<IReadOnlyList<OhlcvRecord>>(Array.Empty<OhlcvRecord>());
    }

    private sealed class ScriptedSignal : IStockSignal
    {
        private readonly Func<StockData, double> _scoreOf;
        public ScriptedSignal(Func<StockData, double> scoreOf) => _scoreOf = scoreOf;
        public string Name => "Scripted";
        public double DefaultWeight => 1.0;
        public Task<SignalResult> EvaluateAsync(StockData stock) =>
            Task.FromResult(new SignalResult(Name, _scoreOf(stock), 1.0, "scripted", true));
    }

    private static PortfolioBacktester MakeBacktester(IHistoricalOhlcvProvider hist, Func<StockData, double> scoreOf) =>
        new(hist, new IStockSignal[] { new ScriptedSignal(scoreOf) }, new ScoreCalculator(),
            NullLogger<PortfolioBacktester>.Instance);

    [Theory]
    [InlineData(0, 100, 100)]
    [InlineData(10, 100, 100.10)]   // 10 bps = 0.10%
    [InlineData(50, 100, 100.50)]
    public void ApplySlippage_BuyRaisesPrice(double bps, double price, double expected)
    {
        var actual = PortfolioBacktester.ApplySlippage((decimal)price, bps, isBuy: true);
        Assert.Equal((decimal)expected, actual);
    }

    [Theory]
    [InlineData(10, 100, 99.90)]
    [InlineData(50, 100, 99.50)]
    public void ApplySlippage_SellLowersPrice(double bps, double price, double expected)
    {
        var actual = PortfolioBacktester.ApplySlippage((decimal)price, bps, isBuy: false);
        Assert.Equal((decimal)expected, actual);
    }

    [Fact]
    public async Task SlippageOnEntry_RaisesEntryPriceInTradeLog()
    {
        var hist = new FakeHist();
        hist.Add("AAPL", new[]
        {
            Bar("AAPL", new DateOnly(2024, 1, 1), 100, 101, 99, 100),
            Bar("AAPL", new DateOnly(2024, 1, 2), 100, 101, 99, 100),
        });

        // 50 bps slippage = 0.50% → entry @ $100.50
        var strategy = StrategyParameters.Default with
        {
            ExitOnSignalReversal = false,
            StopLossPct = 0.5m,
            TakeProfitPct = 0.5m,
            SlippageBps = 50,
        };
        var backtester = MakeBacktester(hist, _ => 0.9);

        var request = new PortfolioBacktestRequest(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 2),
            100_000m, new[] { "AAPL" }, strategy);

        var result = await backtester.RunAsync(request);

        Assert.Single(result.Trades);
        Assert.Equal(100.50m, result.Trades[0].EntryPrice);
    }

    [Fact]
    public async Task SlippageOnExit_LowersExitPriceInTradeLog()
    {
        var hist = new FakeHist();
        hist.Add("AAPL", new[]
        {
            Bar("AAPL", new DateOnly(2024, 1, 1), 100, 101, 99, 100),
            Bar("AAPL", new DateOnly(2024, 1, 2), 100, 102, 98, 99),
        });

        // Entry day 1, signal flips negative day 2 → close at day 2's close
        // with 50 bps slippage on the sell side: $99 * (1 - 0.005) = $98.505
        var strategy = StrategyParameters.Default with { SlippageBps = 50 };
        var backtester = MakeBacktester(hist, sd =>
            sd.OhlcvHistory.Last().Date == new DateOnly(2024, 1, 1) ? 0.9 : -0.5);

        var request = new PortfolioBacktestRequest(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 2),
            100_000m, new[] { "AAPL" }, strategy);

        var result = await backtester.RunAsync(request);

        Assert.Single(result.Trades);
        Assert.Equal(98.505m, result.Trades[0].ExitPrice);
    }

    [Fact]
    public async Task Commission_DeductedFromRealizedPnL()
    {
        var hist = new FakeHist();
        hist.Add("AAPL", new[]
        {
            Bar("AAPL", new DateOnly(2024, 1, 1), 100, 101, 99, 100),
            Bar("AAPL", new DateOnly(2024, 1, 2), 100, 102, 98, 100),  // exit at $100
        });

        var strategy = StrategyParameters.Default with
        {
            ExitOnSignalReversal = false,
            StopLossPct = 0.5m,
            TakeProfitPct = 0.5m,
            CommissionPerTrade = 5m,
        };
        var backtester = MakeBacktester(hist, _ => 0.9);

        var request = new PortfolioBacktestRequest(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 2),
            100_000m, new[] { "AAPL" }, strategy);

        var result = await backtester.RunAsync(request);

        Assert.Single(result.Trades);
        var trade = result.Trades[0];
        // No price move ($100 → $100), so gross P&L is 0; commission of $5 on
        // exit makes realized P&L = -$5. (Entry commission is deducted from
        // cash but doesn't appear in the trade's realized P&L.)
        Assert.Equal(-5m, trade.RealizedPnL);
    }

    [Fact]
    public async Task CashDrag_IncreasesCashOverTime_NoTradesEnteringEverything()
    {
        var hist = new FakeHist();
        // 4 days of bars, ticker is in universe but score never crosses threshold,
        // so no trades. Cash should accrue at the risk-free rate.
        hist.Add("AAPL", new[]
        {
            Bar("AAPL", new DateOnly(2024, 1, 1), 100, 101, 99, 100),
            Bar("AAPL", new DateOnly(2024, 1, 2), 100, 101, 99, 100),
            Bar("AAPL", new DateOnly(2024, 1, 3), 100, 101, 99, 100),
            Bar("AAPL", new DateOnly(2024, 1, 4), 100, 101, 99, 100),
        });

        var strategy = StrategyParameters.Default with
        {
            RiskFreeAnnualRate = 0.10,  // 10% annual for visible numbers
        };
        var backtester = MakeBacktester(hist, _ => 0.0);  // never enter

        var request = new PortfolioBacktestRequest(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 4),
            100_000m, new[] { "AAPL" }, strategy);

        var result = await backtester.RunAsync(request);

        // 3 calendar gaps (day 1→2, 2→3, 3→4) each accruing 10%/365 daily.
        // Approximate factor: 1 + (0.10/365) × 3 ≈ 1.00082 → ~$100,082
        Assert.True(result.EquityCurve[^1].Cash > 100_000m);
        Assert.True(result.EquityCurve[^1].Cash < 100_200m);
    }

    [Fact]
    public async Task RealismDefaultsZero_BehaviorMatchesQ4()
    {
        // Sanity: with all Q7 fields at 0 (the default), behavior is identical
        // to Q4 — entry @ raw close, exit @ raw price, no commission, no drag.
        var hist = new FakeHist();
        hist.Add("AAPL", new[]
        {
            Bar("AAPL", new DateOnly(2024, 1, 1), 100, 101, 99, 100),
            Bar("AAPL", new DateOnly(2024, 1, 2), 105, 125, 104, 122),  // hits target
        });

        var backtester = MakeBacktester(hist, _ => 0.9);
        var request = new PortfolioBacktestRequest(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 2),
            100_000m, new[] { "AAPL" }, StrategyParameters.Default);

        var result = await backtester.RunAsync(request);

        Assert.Single(result.Trades);
        Assert.Equal(100m, result.Trades[0].EntryPrice);
        Assert.Equal(120m, result.Trades[0].ExitPrice);
    }
}

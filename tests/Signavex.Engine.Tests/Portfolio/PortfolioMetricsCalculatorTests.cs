using Signavex.Domain.Models.Portfolio;
using Signavex.Engine.Portfolio;

namespace Signavex.Engine.Tests.Portfolio;

/// <summary>
/// Q5: pure-math validation for Sharpe, max drawdown, annualized return,
/// monthly P&amp;L grouping, and per-ticker breakdown. Tests are independent
/// of the simulator — input is canned trades + equity curves.
/// </summary>
public class PortfolioMetricsCalculatorTests
{
    private static EquityPoint Pt(DateOnly date, decimal equity) =>
        new(date, Cash: equity, PositionsValue: 0m, TotalEquity: equity, OpenPositionCount: 0);

    private static Trade Tr(string ticker, DateOnly entry, DateOnly exit, decimal entryPx, decimal exitPx, int shares = 10) =>
        new(ticker, shares, entry, entryPx, exit, exitPx, TradeExitReason.SignalReversal, (exitPx - entryPx) * shares);

    [Fact]
    public void AnnualizedReturn_Approx10PctOver1Year_Returns10Pct()
    {
        var curve = new[]
        {
            Pt(new DateOnly(2024, 1, 1), 100_000m),
            Pt(new DateOnly(2025, 1, 1), 110_000m),
        };

        var totalReturn = 0.10;
        var annualized = PortfolioMetricsCalculator.ComputeAnnualizedReturn(curve, totalReturn);

        // 2024 is a leap year, so the elapsed window is 366 days. Use a slightly
        // wider tolerance than 365-day-exact would imply.
        Assert.InRange(annualized, 0.0995, 0.1005);
    }

    [Fact]
    public void AnnualizedReturn_HalfYear20Pct_AnnualizesUp()
    {
        var curve = new[]
        {
            Pt(new DateOnly(2024, 1, 1), 100_000m),
            Pt(new DateOnly(2024, 7, 1), 120_000m),  // ~6 months
        };

        var annualized = PortfolioMetricsCalculator.ComputeAnnualizedReturn(curve, 0.20);

        // (1.20)^2 - 1 ≈ 0.44 — but actual day count is 182/365, so closer to 0.439
        Assert.InRange(annualized, 0.40, 0.50);
    }

    [Fact]
    public void AnnualizedReturn_LessThan2Points_ReturnsZero()
    {
        var curve = new[] { Pt(new DateOnly(2024, 1, 1), 100_000m) };
        Assert.Equal(0.0, PortfolioMetricsCalculator.ComputeAnnualizedReturn(curve, 0.10));
    }

    [Fact]
    public void Sharpe_FlatEquity_ReturnsZero()
    {
        var curve = Enumerable.Range(0, 30)
            .Select(i => Pt(new DateOnly(2024, 1, 1).AddDays(i), 100_000m))
            .ToList();

        Assert.Equal(0.0, PortfolioMetricsCalculator.ComputeSharpe(curve));
    }

    [Fact]
    public void Sharpe_SteadyGains_PositiveAndFinite()
    {
        // Each day +0.1% — small steady gains, very high Sharpe
        var curve = new List<EquityPoint>();
        decimal eq = 100_000m;
        for (int i = 0; i < 30; i++)
        {
            curve.Add(Pt(new DateOnly(2024, 1, 1).AddDays(i), eq));
            eq *= 1.001m;
        }

        var sharpe = PortfolioMetricsCalculator.ComputeSharpe(curve);

        // Steady positive returns with near-zero variance → very high Sharpe.
        // We don't pin a specific value; just assert it's strongly positive.
        Assert.True(sharpe > 5, $"Expected Sharpe > 5, got {sharpe}");
    }

    [Fact]
    public void MaxDrawdown_NoDrop_ReturnsZero()
    {
        var curve = new[]
        {
            Pt(new DateOnly(2024, 1, 1), 100m),
            Pt(new DateOnly(2024, 1, 2), 110m),
            Pt(new DateOnly(2024, 1, 3), 120m),
        };

        Assert.Equal(0.0, PortfolioMetricsCalculator.ComputeMaxDrawdown(curve));
    }

    [Fact]
    public void MaxDrawdown_PeakToTrough_ReturnsCorrectFraction()
    {
        // Peak 200, trough 150 → 25% drawdown
        var curve = new[]
        {
            Pt(new DateOnly(2024, 1, 1), 100m),
            Pt(new DateOnly(2024, 1, 2), 200m),  // peak
            Pt(new DateOnly(2024, 1, 3), 150m),  // trough
            Pt(new DateOnly(2024, 1, 4), 180m),
        };

        var dd = PortfolioMetricsCalculator.ComputeMaxDrawdown(curve);
        Assert.Equal(0.25, dd, 4);
    }

    [Fact]
    public void MaxDrawdown_MultipleDrawdowns_PicksDeepest()
    {
        // First DD: 100 → 80 (20%). Second DD: 200 → 100 (50%). Picks 50%.
        var curve = new[]
        {
            Pt(new DateOnly(2024, 1, 1), 100m),
            Pt(new DateOnly(2024, 1, 2), 80m),
            Pt(new DateOnly(2024, 1, 3), 200m),
            Pt(new DateOnly(2024, 1, 4), 100m),
            Pt(new DateOnly(2024, 1, 5), 220m),
        };

        var dd = PortfolioMetricsCalculator.ComputeMaxDrawdown(curve);
        Assert.Equal(0.50, dd, 4);
    }

    [Fact]
    public void MonthlyPnL_GroupsByExitMonth()
    {
        var trades = new[]
        {
            Tr("A", new DateOnly(2024, 1, 5), new DateOnly(2024, 1, 20), 100, 110),  // Jan: +100
            Tr("B", new DateOnly(2024, 1, 5), new DateOnly(2024, 2, 20), 100, 90),   // Feb: -100
            Tr("C", new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 25), 100, 120),  // Feb: +200
        };

        var monthly = PortfolioMetricsCalculator.ComputeMonthlyPnL(trades);

        Assert.Equal(2, monthly.Count);
        Assert.Equal(2024, monthly[0].Year);
        Assert.Equal(1, monthly[0].Month);
        Assert.Equal(100m, monthly[0].RealizedPnL);
        Assert.Equal(2, monthly[1].Month);
        Assert.Equal(100m, monthly[1].RealizedPnL);  // -100 + 200
        Assert.Equal(2, monthly[1].TradeCount);
    }

    [Fact]
    public void PerTickerBreakdown_GroupsByTicker()
    {
        var trades = new[]
        {
            Tr("AAPL", new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 10), 100, 110),  // +100
            Tr("AAPL", new DateOnly(2024, 1, 11), new DateOnly(2024, 1, 20), 110, 105), // -50
            Tr("MSFT", new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 15), 200, 220),  // +200
        };

        var breakdown = PortfolioMetricsCalculator.ComputePerTickerBreakdown(trades);

        Assert.Equal(2, breakdown.Count);
        // Ordered by TotalPnL descending — MSFT first (+200), AAPL second (+50)
        Assert.Equal("MSFT", breakdown[0].Ticker);
        Assert.Equal(200m, breakdown[0].TotalPnL);
        Assert.Equal("AAPL", breakdown[1].Ticker);
        Assert.Equal(50m, breakdown[1].TotalPnL);
        Assert.Equal(0.5, breakdown[1].WinRate);  // 1 of 2 winning
    }

    [Fact]
    public void ComputeMetrics_EmptyEquityCurve_ReturnsEmpty()
    {
        var metrics = PortfolioMetricsCalculator.ComputeMetrics(
            100_000m,
            Array.Empty<Trade>(),
            Array.Empty<EquityPoint>());

        Assert.Equal(100_000m, metrics.StartingEquity);
        Assert.Equal(100_000m, metrics.EndingEquity);
        Assert.Equal(0, metrics.TotalTrades);
    }

    [Fact]
    public void ComputeMetrics_PopulatesAllFields()
    {
        var equityCurve = new[]
        {
            Pt(new DateOnly(2024, 1, 1), 100_000m),
            Pt(new DateOnly(2024, 6, 1), 110_000m),
            Pt(new DateOnly(2024, 12, 31), 120_000m),
        };
        var trades = new[]
        {
            Tr("AAPL", new DateOnly(2024, 1, 5), new DateOnly(2024, 3, 1), 100, 110),
            Tr("AAPL", new DateOnly(2024, 4, 1), new DateOnly(2024, 5, 1), 110, 105),
        };

        var metrics = PortfolioMetricsCalculator.ComputeMetrics(100_000m, trades, equityCurve);

        Assert.Equal(120_000m, metrics.EndingEquity);
        Assert.InRange(metrics.TotalReturnPct, 0.19, 0.21);
        Assert.True(metrics.AnnualizedReturnPct > 0);
        Assert.Equal(2, metrics.TotalTrades);
        Assert.Equal(1, metrics.WinningTrades);
        Assert.Equal(1, metrics.LosingTrades);
    }
}

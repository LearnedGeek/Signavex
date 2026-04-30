using System.Text.Json;
using Signavex.Domain.Models.Portfolio;

namespace Signavex.Domain.Tests.Portfolio;

/// <summary>
/// Quantback persists <see cref="PortfolioBacktestRequest"/> and
/// <see cref="PortfolioBacktestResult"/> as JSON in the QuantbackRuns
/// table so user runs survive App Service restarts. These tests pin the
/// round-trip so a future record change doesn't silently break the
/// persisted-result flow.
/// </summary>
public class PortfolioJsonRoundTripTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void PortfolioBacktestRequest_RoundTrip_PreservesAllFields()
    {
        var original = new PortfolioBacktestRequest(
            StartDate: new DateOnly(2020, 1, 1),
            EndDate: new DateOnly(2025, 1, 1),
            StartingCapital: 250_000.50m,
            Universe: new[] { "AAPL", "MSFT", "GOOGL" },
            Strategy: StrategyParameters.Realistic);

        var json = JsonSerializer.Serialize(original, Options);
        var roundTripped = JsonSerializer.Deserialize<PortfolioBacktestRequest>(json, Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.StartDate, roundTripped!.StartDate);
        Assert.Equal(original.EndDate, roundTripped.EndDate);
        Assert.Equal(original.StartingCapital, roundTripped.StartingCapital);
        Assert.Equal(original.Universe, roundTripped.Universe);
        Assert.Equal(original.Strategy.PositionSizePct, roundTripped.Strategy.PositionSizePct);
        Assert.Equal(original.Strategy.SlippageBps, roundTripped.Strategy.SlippageBps);
        Assert.Equal(original.Strategy.CommissionPerTrade, roundTripped.Strategy.CommissionPerTrade);
    }

    [Fact]
    public void PortfolioBacktestResult_RoundTrip_PreservesTradesAndMetrics()
    {
        var request = new PortfolioBacktestRequest(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31),
            100_000m,
            new[] { "AAPL" },
            StrategyParameters.Default);

        var trade = new Trade(
            Ticker: "AAPL",
            Shares: 50,
            EntryDate: new DateOnly(2024, 1, 5),
            EntryPrice: 100m,
            ExitDate: new DateOnly(2024, 1, 20),
            ExitPrice: 110m,
            ExitReason: TradeExitReason.TakeProfit,
            RealizedPnL: 500m);

        var equity = new EquityPoint(new DateOnly(2024, 1, 5), 95_000m, 5_000m, 100_000m, 1);

        var original = new PortfolioBacktestResult(
            Request: request,
            EquityCurve: new[] { equity },
            Trades: new[] { trade },
            OpenPositions: Array.Empty<Position>(),
            Metrics: new PortfolioBacktestMetrics(
                StartingEquity: 100_000m, EndingEquity: 110_000m,
                TotalReturnPct: 0.10, AnnualizedReturnPct: 0.10,
                SharpeRatio: 1.5, MaxDrawdownPct: 0.05,
                TotalTrades: 1, WinningTrades: 1, LosingTrades: 0,
                WinRate: 1.0, AvgWinPnL: 500m, AvgLossPnL: 0m, AvgHoldDays: 15),
            MonthlyPnL: new[] { new MonthlyPnLPoint(2024, 1, 500m, 1) },
            PerTickerBreakdown: new[] { new TickerStats("AAPL", 1, 1, 500m, 1.0, 15) },
            StartedAt: new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            CompletedAt: new DateTime(2024, 1, 1, 12, 5, 0, DateTimeKind.Utc));

        var json = JsonSerializer.Serialize(original, Options);
        var rt = JsonSerializer.Deserialize<PortfolioBacktestResult>(json, Options);

        Assert.NotNull(rt);
        Assert.Single(rt!.Trades);
        Assert.Equal("AAPL", rt.Trades[0].Ticker);
        Assert.Equal(TradeExitReason.TakeProfit, rt.Trades[0].ExitReason);
        Assert.Equal(500m, rt.Trades[0].RealizedPnL);
        Assert.Single(rt.EquityCurve);
        Assert.Equal(100_000m, rt.EquityCurve[0].TotalEquity);
        Assert.Equal(0.10, rt.Metrics.TotalReturnPct);
        Assert.Equal(1, rt.MonthlyPnL.Count);
        Assert.Equal(1, rt.PerTickerBreakdown.Count);
    }
}

namespace Signavex.Domain.Models.Portfolio;

/// <summary>
/// Full output of a portfolio-simulation backtest. Echoes back the
/// <paramref name="Request"/> for traceability when a result is rendered
/// or persisted.
/// </summary>
public record PortfolioBacktestResult(
    PortfolioBacktestRequest Request,
    IReadOnlyList<EquityPoint> EquityCurve,
    IReadOnlyList<Trade> Trades,
    IReadOnlyList<Position> OpenPositions,
    PortfolioBacktestMetrics Metrics,
    IReadOnlyList<MonthlyPnLPoint> MonthlyPnL,
    IReadOnlyList<TickerStats> PerTickerBreakdown,
    DateTime StartedAt,
    DateTime CompletedAt
)
{
    public static PortfolioBacktestResult Empty(PortfolioBacktestRequest request, DateTime now) => new(
        Request: request,
        EquityCurve: Array.Empty<EquityPoint>(),
        Trades: Array.Empty<Trade>(),
        OpenPositions: Array.Empty<Position>(),
        Metrics: PortfolioBacktestMetrics.Empty(request.StartingCapital),
        MonthlyPnL: Array.Empty<MonthlyPnLPoint>(),
        PerTickerBreakdown: Array.Empty<TickerStats>(),
        StartedAt: now,
        CompletedAt: now);
}

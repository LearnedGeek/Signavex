namespace Signavex.Domain.Models.Portfolio;

/// <summary>
/// Realized P&amp;L aggregated by the month a trade exited. Useful for
/// spotting seasonality, drawdown clusters, or a single bad month
/// dominating an otherwise reasonable strategy.
/// </summary>
public record MonthlyPnLPoint(
    int Year,
    int Month,
    decimal RealizedPnL,
    int TradeCount
);

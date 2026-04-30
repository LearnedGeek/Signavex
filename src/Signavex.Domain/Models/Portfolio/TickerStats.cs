namespace Signavex.Domain.Models.Portfolio;

/// <summary>
/// Per-ticker breakdown so a user can see which names actually contributed
/// to (or detracted from) the strategy. <see cref="TotalPnL"/> sums realized
/// P&amp;L across all closed trades for the ticker; <see cref="WinRate"/> is
/// in [0, 1].
/// </summary>
public record TickerStats(
    string Ticker,
    int TradeCount,
    int WinningTrades,
    decimal TotalPnL,
    double WinRate,
    double AvgHoldDays
);

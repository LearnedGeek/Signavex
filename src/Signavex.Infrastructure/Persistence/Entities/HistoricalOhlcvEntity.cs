namespace Signavex.Infrastructure.Persistence.Entities;

/// <summary>
/// One row per (Ticker, TradingDate). Stores adjusted-close OHLCV from
/// Polygon for the Quantback portfolio backtest. Past trading days never
/// change (corporate actions are already reflected in the adjusted prices),
/// so this cache has no TTL.
/// </summary>
public class HistoricalOhlcvEntity
{
    public int Id { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public DateOnly TradingDate { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
    public DateTime FetchedAtUtc { get; set; }
}

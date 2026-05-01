namespace Signavex.Infrastructure.Persistence.Entities;

/// <summary>
/// One row per (scan, ticker) for the forward-test feature. Captures the
/// score the live picker assigned, the entry price a real user could have
/// gotten (next trading day's close), and SPY's price on the same day for
/// benchmarking. Per-horizon columns are populated lazily by the nightly
/// outcome evaluator job (FT2) as time crosses each threshold.
///
/// Money values use <c>decimal(18,4)</c> to match <c>HistoricalOhlcv</c>;
/// returns and outperformance are <c>double</c> for ratio math.
/// </summary>
public class PickOutcomeEntity
{
    public int Id { get; set; }

    // ── Captured at scan time ───────────────────────────────────────────
    public DateOnly ScanDate { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public double RawScore { get; set; }
    public double FinalScore { get; set; }

    /// <summary>Next trading day after the scan — the day a user could realistically buy at close.</summary>
    public DateOnly? EntryDate { get; set; }
    public decimal? EntryPrice { get; set; }
    public decimal? SpyEntryPrice { get; set; }

    /// <summary>Set when entry resolution failed (e.g., delisted ticker, missing OHLCV).</summary>
    public string? EntrySkippedReason { get; set; }

    // ── Filled by the nightly outcome evaluator (FT2) ───────────────────
    public decimal? Price30d { get; set; }
    public double? TickerReturn30d { get; set; }
    public double? SpyReturn30d { get; set; }
    public double? Outperformance30d { get; set; }

    public decimal? Price90d { get; set; }
    public double? TickerReturn90d { get; set; }
    public double? SpyReturn90d { get; set; }
    public double? Outperformance90d { get; set; }

    public decimal? Price180d { get; set; }
    public double? TickerReturn180d { get; set; }
    public double? SpyReturn180d { get; set; }
    public double? Outperformance180d { get; set; }

    public decimal? Price365d { get; set; }
    public double? TickerReturn365d { get; set; }
    public double? SpyReturn365d { get; set; }
    public double? Outperformance365d { get; set; }

    /// <summary>Bookkeeping: most recent time the evaluator looked at this row.</summary>
    public DateTime? LastEvaluatedAtUtc { get; set; }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Signavex.Domain.Interfaces;
using Signavex.Domain.Models;
using Signavex.Infrastructure.Persistence;
using Signavex.Infrastructure.Persistence.Entities;

namespace Signavex.Jobs.Orchestrators;

/// <summary>
/// FT2: nightly evaluator. Walks <c>PickOutcomes</c> and fills in:
///   1) <c>EntryDate</c>/<c>EntryPrice</c>/<c>SpyEntryPrice</c> for rows
///      that don't have them yet (next-trading-day close after the scan
///      date — uses the historical OHLCV cache so it's near-free after
///      the first warmup),
///   2) per-horizon (30/90/180/365 day) ticker close, SPY close, ticker
///      return, SPY return, and outperformance, for rows whose target
///      date has been reached and whose horizon column is null.
///
/// Runs daily; idempotent (a row already filled for a horizon is left
/// alone; only nulls get written). Logs counts so we can monitor the
/// backlog from Application Insights.
/// </summary>
public class PickOutcomeEvaluatorOrchestrator
{
    private const string SpyTicker = "SPY";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PickOutcomeEvaluatorOrchestrator> _logger;

    public PickOutcomeEvaluatorOrchestrator(
        IServiceScopeFactory scopeFactory,
        ILogger<PickOutcomeEvaluatorOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<EvaluationResult> RunCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SignavexDbContext>>();
        var historical = scope.ServiceProvider.GetRequiredService<IHistoricalOhlcvProvider>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Pull all rows with at least one piece of work outstanding. The
        // EntryDate index covers the (EntryDate IS NULL) branch; the rest
        // are scanned but the table is small relative to (e.g.) HistoricalOhlcv.
        var pending = await db.PickOutcomes
            .Where(p =>
                p.EntryDate == null ||
                (p.Price30d == null) ||
                (p.Price90d == null) ||
                (p.Price180d == null) ||
                (p.Price365d == null))
            .OrderBy(p => p.ScanDate)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            _logger.LogInformation("FT2: no pick outcomes need evaluation.");
            return new EvaluationResult(0, 0, 0, 0);
        }

        // Range for the SPY benchmark — covers the earliest ScanDate to today.
        var earliestScan = pending.Min(p => p.ScanDate);
        var spyBars = await historical.GetHistoricalDailyOhlcvAsync(SpyTicker, earliestScan, today, ct);
        var spyBarsList = spyBars as IReadOnlyList<OhlcvRecord> ?? spyBars.ToList();
        if (spyBarsList.Count == 0)
        {
            _logger.LogWarning("FT2: SPY OHLCV unavailable for {From}–{To}; cannot benchmark this cycle.", earliestScan, today);
            return new EvaluationResult(0, 0, 0, pending.Count);
        }

        // Group by ticker so we fetch each ticker's bars once for the cycle.
        var byTicker = pending.GroupBy(p => p.Ticker, StringComparer.OrdinalIgnoreCase).ToList();

        var rowsTouched = 0;
        var horizonsFilled = 0;
        var entriesResolved = 0;
        var errors = 0;

        foreach (var group in byTicker)
        {
            ct.ThrowIfCancellationRequested();
            var ticker = group.Key;
            var rows = group.ToList();
            var groupEarliest = rows.Min(r => r.ScanDate);

            IReadOnlyList<OhlcvRecord> bars;
            try
            {
                var fetched = await historical.GetHistoricalDailyOhlcvAsync(ticker, groupEarliest, today, ct);
                bars = fetched as IReadOnlyList<OhlcvRecord> ?? fetched.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FT2: OHLCV fetch failed for {Ticker} — skipping this cycle", ticker);
                errors += rows.Count;
                continue;
            }

            if (bars.Count == 0)
            {
                // Could be delisted, halted, or just not in the cache. Try
                // again next cycle; logging keeps the operator aware.
                _logger.LogInformation("FT2: no bars for {Ticker} {From}–{To} — skipping {Rows} rows", ticker, groupEarliest, today, rows.Count);
                continue;
            }

            foreach (var row in rows)
            {
                rowsTouched++;
                row.LastEvaluatedAtUtc = DateTime.UtcNow;

                // Step 1: resolve entry if needed.
                if (row.EntryDate is null)
                {
                    var entryBar = FirstBarOnOrAfter(bars, row.ScanDate.AddDays(1));
                    var spyEntryBar = FirstBarOnOrAfter(spyBarsList, row.ScanDate.AddDays(1));
                    if (entryBar is null || spyEntryBar is null)
                    {
                        // Picks made literally yesterday haven't had their
                        // entry day close yet — totally normal. Try next cycle.
                        continue;
                    }
                    row.EntryDate = entryBar.Date;
                    row.EntryPrice = entryBar.Close;
                    row.SpyEntryPrice = spyEntryBar.Close;
                    entriesResolved++;
                }

                // Step 2: evaluate any matured horizons.
                horizonsFilled += TryFillHorizon(row, bars, spyBarsList, today, 30,
                    setPrice: (r, p) => r.Price30d = p,
                    setTickerReturn: (r, v) => r.TickerReturn30d = v,
                    setSpyReturn: (r, v) => r.SpyReturn30d = v,
                    setOutperformance: (r, v) => r.Outperformance30d = v,
                    isFilled: r => r.Price30d != null);

                horizonsFilled += TryFillHorizon(row, bars, spyBarsList, today, 90,
                    setPrice: (r, p) => r.Price90d = p,
                    setTickerReturn: (r, v) => r.TickerReturn90d = v,
                    setSpyReturn: (r, v) => r.SpyReturn90d = v,
                    setOutperformance: (r, v) => r.Outperformance90d = v,
                    isFilled: r => r.Price90d != null);

                horizonsFilled += TryFillHorizon(row, bars, spyBarsList, today, 180,
                    setPrice: (r, p) => r.Price180d = p,
                    setTickerReturn: (r, v) => r.TickerReturn180d = v,
                    setSpyReturn: (r, v) => r.SpyReturn180d = v,
                    setOutperformance: (r, v) => r.Outperformance180d = v,
                    isFilled: r => r.Price180d != null);

                horizonsFilled += TryFillHorizon(row, bars, spyBarsList, today, 365,
                    setPrice: (r, p) => r.Price365d = p,
                    setTickerReturn: (r, v) => r.TickerReturn365d = v,
                    setSpyReturn: (r, v) => r.SpyReturn365d = v,
                    setOutperformance: (r, v) => r.Outperformance365d = v,
                    isFilled: r => r.Price365d != null);
            }
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "FT2 cycle complete: {Pending} pending → {Touched} rows touched, {Entries} entries resolved, {Filled} horizons filled, {Errors} errors",
            pending.Count, rowsTouched, entriesResolved, horizonsFilled, errors);

        return new EvaluationResult(rowsTouched, entriesResolved, horizonsFilled, errors);
    }

    private static int TryFillHorizon(
        PickOutcomeEntity row,
        IReadOnlyList<OhlcvRecord> tickerBars,
        IReadOnlyList<OhlcvRecord> spyBars,
        DateOnly today,
        int horizonDays,
        Action<PickOutcomeEntity, decimal> setPrice,
        Action<PickOutcomeEntity, double> setTickerReturn,
        Action<PickOutcomeEntity, double> setSpyReturn,
        Action<PickOutcomeEntity, double> setOutperformance,
        Func<PickOutcomeEntity, bool> isFilled)
    {
        if (isFilled(row)) return 0;
        if (row.EntryDate is not DateOnly entry) return 0;
        if (row.EntryPrice is not decimal entryPrice || row.SpyEntryPrice is not decimal spyEntry) return 0;

        var target = entry.AddDays(horizonDays);
        if (target > today) return 0;  // Not matured yet.

        var tickerBar = FirstBarOnOrAfter(tickerBars, target);
        var spyBar = FirstBarOnOrAfter(spyBars, target);
        if (tickerBar is null || spyBar is null) return 0;

        var tickerReturn = entryPrice == 0 ? 0.0 : (double)((tickerBar.Close - entryPrice) / entryPrice);
        var spyReturn = spyEntry == 0 ? 0.0 : (double)((spyBar.Close - spyEntry) / spyEntry);

        setPrice(row, tickerBar.Close);
        setTickerReturn(row, tickerReturn);
        setSpyReturn(row, spyReturn);
        setOutperformance(row, tickerReturn - spyReturn);
        return 1;
    }

    /// <summary>
    /// First bar on or after <paramref name="target"/>. Bars are assumed
    /// sorted ascending by date (which is what our providers return).
    /// Linear scan is fine — typical bar lists are O(few hundred).
    /// </summary>
    internal static OhlcvRecord? FirstBarOnOrAfter(IReadOnlyList<OhlcvRecord> bars, DateOnly target)
    {
        for (int i = 0; i < bars.Count; i++)
        {
            if (bars[i].Date >= target) return bars[i];
        }
        return null;
    }
}

public record EvaluationResult(int RowsTouched, int EntriesResolved, int HorizonsFilled, int Errors);

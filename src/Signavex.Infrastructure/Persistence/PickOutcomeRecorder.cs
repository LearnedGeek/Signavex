using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Signavex.Domain.Interfaces;
using Signavex.Domain.Models;
using Signavex.Infrastructure.Persistence.Entities;

namespace Signavex.Infrastructure.Persistence;

/// <summary>
/// Writes one <see cref="PickOutcomeEntity"/> per candidate per scan.
/// Captures all candidates regardless of surfacing threshold so future
/// analysis can ask "did high-score picks actually outperform low-score
/// picks?" — that requires the full distribution, not just the surfaced
/// subset.
/// </summary>
public class PickOutcomeRecorder : IPickOutcomeRecorder
{
    private readonly IDbContextFactory<SignavexDbContext> _dbFactory;
    private readonly ILogger<PickOutcomeRecorder> _logger;

    public PickOutcomeRecorder(
        IDbContextFactory<SignavexDbContext> dbFactory,
        ILogger<PickOutcomeRecorder> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task RecordScanAsync(
        DateOnly scanDate,
        IEnumerable<StockCandidate> candidates,
        CancellationToken ct = default)
    {
        var list = candidates as IList<StockCandidate> ?? candidates.ToList();
        if (list.Count == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Pull tickers that already have a row for this scan date so we don't
        // duplicate. Cheap query — ScanDate index covers it.
        var existing = await db.PickOutcomes
            .Where(p => p.ScanDate == scanDate)
            .Select(p => p.Ticker)
            .ToListAsync(ct);
        var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var newRows = 0;
        foreach (var c in list)
        {
            if (existingSet.Contains(c.Ticker)) continue;

            db.PickOutcomes.Add(new PickOutcomeEntity
            {
                ScanDate = scanDate,
                Ticker = c.Ticker,
                RawScore = c.RawScore,
                FinalScore = c.FinalScore,
                // EntryDate/EntryPrice/SpyEntryPrice and all horizon columns
                // are filled by the FT2 nightly evaluator once data is
                // available (next trading day's close hasn't happened yet
                // at the moment the scan completes).
            });
            newRows++;
        }

        if (newRows > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Recorded {New} new pick outcomes for scan date {Date}", newRows, scanDate);
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Signavex.Domain.Enums;
using Signavex.Domain.Interfaces;
using Signavex.Domain.Models;
using Signavex.Infrastructure.Persistence;

namespace Signavex.Jobs.Orchestrators;

/// <summary>
/// FT3: one-shot retroactive populate of <c>PickOutcomes</c> from existing
/// <c>ScanRuns</c>/<c>ScanCandidates</c>. Idempotent — leans on the
/// recorder's first-write-wins semantics, so re-running adds rows for any
/// scans missed by prior calls and skips ones already captured.
///
/// After the backfill, the FT2 evaluator can be invoked (or wait for its
/// nightly trigger) to resolve entries and grade matured horizons. The
/// payoff: instead of waiting 30+ days for the first FT4 dashboard data,
/// you get every horizon that's already in the past at runtime.
/// </summary>
public class PickOutcomeBackfillOrchestrator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PickOutcomeBackfillOrchestrator> _logger;

    public PickOutcomeBackfillOrchestrator(
        IServiceScopeFactory scopeFactory,
        ILogger<PickOutcomeBackfillOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<BackfillResult> RunAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SignavexDbContext>>();
        var recorder = scope.ServiceProvider.GetRequiredService<IPickOutcomeRecorder>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Load all scan runs in chronological order so the unique index on
        // (ScanDate, Ticker) catches duplicates even if multiple runs landed
        // on the same calendar day (rare but possible).
        var runs = await db.ScanRuns
            .AsNoTracking()
            .Include(r => r.Candidates)
            .OrderBy(r => r.CompletedAtUtc)
            .ToListAsync(ct);

        if (runs.Count == 0)
        {
            _logger.LogInformation("FT3 backfill: no scan runs found.");
            return new BackfillResult(0, 0, 0);
        }

        var scansProcessed = 0;
        var candidatesPersisted = 0;

        foreach (var run in runs)
        {
            ct.ThrowIfCancellationRequested();
            var scanDate = DateOnly.FromDateTime(run.CompletedAtUtc);

            // Convert entity → domain candidate. Most fields are vestigial
            // for the recorder's purposes (it only reads Ticker / RawScore /
            // FinalScore) but we hydrate the rest defensively in case the
            // recorder's contract grows.
            var candidates = run.Candidates.Select(c => new StockCandidate(
                Ticker: c.Ticker,
                CompanyName: c.CompanyName,
                Tier: (MarketTier)c.Tier,
                RawScore: c.RawScore,
                FinalScore: c.FinalScore,
                SignalResults: Array.Empty<SignalResult>(),
                MarketContext: new MarketContext(run.MarketMultiplier, run.MarketSummary, Array.Empty<SignalResult>()),
                EvaluatedAt: c.EvaluatedAt)).ToList();

            // Count how many were missing before so we can report a real
            // delta rather than the total pile.
            var existing = await db.PickOutcomes
                .CountAsync(p => p.ScanDate == scanDate, ct);
            var beforeAdd = existing;

            await recorder.RecordScanAsync(scanDate, candidates, ct);

            await using var verify = await dbFactory.CreateDbContextAsync(ct);
            var afterAdd = await verify.PickOutcomes.CountAsync(p => p.ScanDate == scanDate, ct);

            scansProcessed++;
            candidatesPersisted += afterAdd - beforeAdd;
        }

        _logger.LogInformation(
            "FT3 backfill complete: {Scans} scan runs visited, {New} new pick outcomes recorded.",
            scansProcessed, candidatesPersisted);

        return new BackfillResult(scansProcessed, candidatesPersisted, runs.Count);
    }
}

public record BackfillResult(int ScansProcessed, int NewRowsPersisted, int TotalScansFound);

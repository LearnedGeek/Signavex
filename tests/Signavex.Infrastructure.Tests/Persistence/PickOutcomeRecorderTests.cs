using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Signavex.Domain.Enums;
using Signavex.Domain.Models;
using Signavex.Infrastructure.Persistence;

namespace Signavex.Infrastructure.Tests.Persistence;

/// <summary>
/// FT1: per-scan capture of pick outcomes. Captures all candidates
/// regardless of surfacing threshold; idempotent on (ScanDate, Ticker).
/// </summary>
public class PickOutcomeRecorderTests : IAsyncDisposable
{
    private readonly IDbContextFactory<SignavexDbContext> _factory;
    private readonly PickOutcomeRecorder _recorder;

    public PickOutcomeRecorderTests()
    {
        var dbName = $"signavex-pick-test-{Guid.NewGuid():N}.db";
        var options = new DbContextOptionsBuilder<SignavexDbContext>()
            .UseSqlite($"Data Source={dbName}")
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        _recorder = new PickOutcomeRecorder(_factory, NullLogger<PickOutcomeRecorder>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureDeletedAsync();
    }

    private static StockCandidate Candidate(string ticker, double rawScore, double finalScore) =>
        new(
            Ticker: ticker,
            CompanyName: ticker,
            Tier: MarketTier.SP500,
            RawScore: rawScore,
            FinalScore: finalScore,
            SignalResults: Array.Empty<SignalResult>(),
            MarketContext: new MarketContext(1.0, "stub", Array.Empty<SignalResult>()),
            EvaluatedAt: DateTime.UtcNow);

    [Fact]
    public async Task RecordScanAsync_PersistsAllCandidates()
    {
        var scanDate = new DateOnly(2026, 5, 1);
        var candidates = new[]
        {
            Candidate("AAPL", 0.6, 0.65),
            Candidate("MSFT", 0.5, 0.55),
            Candidate("GOOGL", 0.3, 0.32),  // below default threshold
        };

        await _recorder.RecordScanAsync(scanDate, candidates);

        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.PickOutcomes.Where(p => p.ScanDate == scanDate).ToListAsync();

        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Ticker == "AAPL");
        Assert.Contains(rows, r => r.Ticker == "MSFT");
        // Capture below-threshold rows too — they're needed for score-bucket
        // analysis later (FT4).
        Assert.Contains(rows, r => r.Ticker == "GOOGL");
    }

    [Fact]
    public async Task RecordScanAsync_StoresScoresAccurately()
    {
        var scanDate = new DateOnly(2026, 5, 1);
        await _recorder.RecordScanAsync(scanDate, new[] { Candidate("AAPL", 0.62345, 0.71234) });

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.PickOutcomes.SingleAsync(p => p.Ticker == "AAPL");

        Assert.Equal(0.62345, row.RawScore);
        Assert.Equal(0.71234, row.FinalScore);
        // Entry + horizon columns are deferred to FT2.
        Assert.Null(row.EntryDate);
        Assert.Null(row.EntryPrice);
        Assert.Null(row.Price30d);
    }

    [Fact]
    public async Task RecordScanAsync_IdempotentOnRepeat()
    {
        var scanDate = new DateOnly(2026, 5, 1);
        var candidates = new[] { Candidate("AAPL", 0.6, 0.65), Candidate("MSFT", 0.5, 0.55) };

        await _recorder.RecordScanAsync(scanDate, candidates);
        await _recorder.RecordScanAsync(scanDate, candidates);  // repeat

        await using var db = await _factory.CreateDbContextAsync();
        var count = await db.PickOutcomes.CountAsync(p => p.ScanDate == scanDate);
        Assert.Equal(2, count);  // not 4
    }

    [Fact]
    public async Task RecordScanAsync_SeparateScanDates_CoexistFreely()
    {
        var day1 = new DateOnly(2026, 5, 1);
        var day2 = new DateOnly(2026, 5, 2);

        await _recorder.RecordScanAsync(day1, new[] { Candidate("AAPL", 0.6, 0.65) });
        await _recorder.RecordScanAsync(day2, new[] { Candidate("AAPL", 0.7, 0.75) });

        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.PickOutcomes.Where(p => p.Ticker == "AAPL").OrderBy(p => p.ScanDate).ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(0.65, rows[0].FinalScore);
        Assert.Equal(0.75, rows[1].FinalScore);
    }

    [Fact]
    public async Task RecordScanAsync_EmptyCandidates_NoOp()
    {
        var scanDate = new DateOnly(2026, 5, 1);
        await _recorder.RecordScanAsync(scanDate, Array.Empty<StockCandidate>());

        await using var db = await _factory.CreateDbContextAsync();
        var count = await db.PickOutcomes.CountAsync();
        Assert.Equal(0, count);
    }

    private class TestDbContextFactory : IDbContextFactory<SignavexDbContext>
    {
        private readonly DbContextOptions<SignavexDbContext> _options;
        public TestDbContextFactory(DbContextOptions<SignavexDbContext> options) => _options = options;
        public SignavexDbContext CreateDbContext() => new(_options);
    }

    [Fact]
    public async Task RecordScanAsync_PartialOverlap_OnlyAddsNewTickers()
    {
        var scanDate = new DateOnly(2026, 5, 1);
        await _recorder.RecordScanAsync(scanDate, new[] { Candidate("AAPL", 0.6, 0.65) });

        // Re-run with AAPL + new ticker MSFT — AAPL should be skipped, MSFT added.
        await _recorder.RecordScanAsync(scanDate, new[]
        {
            Candidate("AAPL", 0.7, 0.75),  // updated score, but should be ignored
            Candidate("MSFT", 0.5, 0.55),
        });

        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.PickOutcomes.Where(p => p.ScanDate == scanDate).ToListAsync();

        Assert.Equal(2, rows.Count);
        var aapl = rows.Single(r => r.Ticker == "AAPL");
        // Original AAPL row preserved — recorder is "first write wins" for
        // a given (date, ticker), preventing accidental score rewrites.
        Assert.Equal(0.65, aapl.FinalScore);
    }
}

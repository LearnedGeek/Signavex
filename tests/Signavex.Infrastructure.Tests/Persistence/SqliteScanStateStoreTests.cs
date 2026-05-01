using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Signavex.Domain.Configuration;
using Signavex.Domain.Models;
using Signavex.Infrastructure.Persistence;

namespace Signavex.Infrastructure.Tests.Persistence;

public class SqliteScanStateStoreTests : IAsyncDisposable
{
    private readonly IDbContextFactory<SignavexDbContext> _factory;
    private readonly SqliteScanStateStore _store;

    public SqliteScanStateStoreTests()
    {
        var dbName = $"signavex-test-{Guid.NewGuid():N}.db";
        var options = new DbContextOptionsBuilder<SignavexDbContext>()
            .UseSqlite($"Data Source={dbName}")
            .Options;

        _factory = new TestDbContextFactory(options);

        // Create schema
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        // Use the real recorder rather than a mock so SaveCompletedResultAsync
        // exercises the FT1 capture path end-to-end (verified in
        // PickOutcomeRecorderTests).
        var recorder = new PickOutcomeRecorder(_factory, NullLogger<PickOutcomeRecorder>.Instance);
        _store = new SqliteScanStateStore(_factory, NullLogger<SqliteScanStateStore>.Instance,
            Options.Create(new SignavexOptions()), recorder);
    }

    public async ValueTask DisposeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureDeletedAsync();
    }

    private static MarketContext CreateMarketContext() =>
        new(1.2, "Bullish", new[] { new SignalResult("MarketTrend", 0.8, 2.0, "uptrend", true) });

    private static StockCandidate CreateCandidate(string ticker) =>
        new(ticker, $"{ticker} Inc", Domain.Enums.MarketTier.SP500, 0.75, 0.80,
            new[] { new SignalResult("Volume", 0.6, 1.0, "above average", true) },
            CreateMarketContext(), DateTime.UtcNow);

    [Fact]
    public async Task SaveAndLoadCheckpoint_RoundTrips()
    {
        var checkpoint = new ScanCheckpoint(
            "abc123", DateTime.UtcNow, CreateMarketContext(),
            new[] { "AAPL", "MSFT", "GOOG" },
            new[] { "AAPL" },
            new[] { CreateCandidate("AAPL") },
            0);

        await _store.SaveCheckpointAsync(checkpoint);
        var loaded = await _store.LoadCheckpointAsync();

        Assert.NotNull(loaded);
        Assert.Equal("abc123", loaded.ScanId);
        Assert.Equal(3, loaded.UniverseTickers.Count);
        Assert.Single(loaded.EvaluatedTickers);
        Assert.Single(loaded.CandidatesSoFar);
        Assert.Equal("AAPL", loaded.CandidatesSoFar[0].Ticker);
    }

    [Fact]
    public async Task LoadCheckpoint_Empty_ReturnsNull()
    {
        var result = await _store.LoadCheckpointAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteCheckpoint_RemovesRow()
    {
        var checkpoint = new ScanCheckpoint(
            "del1", DateTime.UtcNow, CreateMarketContext(),
            Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<StockCandidate>(), 0);

        await _store.SaveCheckpointAsync(checkpoint);
        Assert.NotNull(await _store.LoadCheckpointAsync());

        await _store.DeleteCheckpointAsync();
        Assert.Null(await _store.LoadCheckpointAsync());
    }

    [Fact]
    public async Task SaveCheckpoint_UpsertsExistingRow()
    {
        var cp1 = new ScanCheckpoint(
            "scan1", DateTime.UtcNow, CreateMarketContext(),
            new[] { "AAPL", "MSFT" }, new[] { "AAPL" },
            new[] { CreateCandidate("AAPL") }, 0);

        await _store.SaveCheckpointAsync(cp1);

        var cp2 = new ScanCheckpoint(
            "scan1", DateTime.UtcNow, CreateMarketContext(),
            new[] { "AAPL", "MSFT" }, new[] { "AAPL", "MSFT" },
            new[] { CreateCandidate("AAPL"), CreateCandidate("MSFT") }, 0);

        await _store.SaveCheckpointAsync(cp2);

        var loaded = await _store.LoadCheckpointAsync();
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.EvaluatedTickers.Count);

        // Verify only one row exists
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.ScanCheckpoints.CountAsync());
    }

    [Fact]
    public async Task SaveAndLoadCompletedResult_RoundTrips()
    {
        var result = new CompletedScanResult(
            "res1", DateTime.UtcNow, CreateMarketContext(),
            new[] { CreateCandidate("AAPL"), CreateCandidate("MSFT") },
            505, 3);

        await _store.SaveCompletedResultAsync(result);
        var loaded = await _store.LoadLatestResultAsync();

        Assert.NotNull(loaded);
        Assert.Equal("res1", loaded.ScanId);
        Assert.Equal(2, loaded.Candidates.Count);
        Assert.Equal(505, loaded.TotalEvaluated);
        Assert.Equal(3, loaded.ErrorCount);
    }

    [Fact]
    public async Task LoadLatestResult_Empty_ReturnsNull()
    {
        var result = await _store.LoadLatestResultAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveCompletedResult_PreservesHistory()
    {
        var result1 = new CompletedScanResult(
            "first", DateTime.UtcNow.AddHours(-1), CreateMarketContext(),
            new[] { CreateCandidate("AAPL") }, 500, 0);

        var result2 = new CompletedScanResult(
            "second", DateTime.UtcNow, CreateMarketContext(),
            new[] { CreateCandidate("MSFT") }, 505, 1);

        await _store.SaveCompletedResultAsync(result1);
        await _store.SaveCompletedResultAsync(result2);

        // LoadLatest returns the most recent
        var loaded = await _store.LoadLatestResultAsync();
        Assert.NotNull(loaded);
        Assert.Equal("second", loaded.ScanId);

        // Both runs are in the database
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await db.ScanRuns.CountAsync());
    }

    [Fact]
    public async Task SaveCompletedResult_CandidatesHaveSignalData()
    {
        var result = new CompletedScanResult(
            "sig1", DateTime.UtcNow, CreateMarketContext(),
            new[] { CreateCandidate("AAPL") }, 1, 0);

        await _store.SaveCompletedResultAsync(result);
        var loaded = await _store.LoadLatestResultAsync();

        Assert.NotNull(loaded);
        var candidate = loaded.Candidates[0];
        Assert.Equal("AAPL", candidate.Ticker);
        Assert.Single(candidate.SignalResults);
        Assert.Equal("Volume", candidate.SignalResults.First().SignalName);
    }

    private class TestDbContextFactory : IDbContextFactory<SignavexDbContext>
    {
        private readonly DbContextOptions<SignavexDbContext> _options;

        public TestDbContextFactory(DbContextOptions<SignavexDbContext> options)
        {
            _options = options;
        }

        public SignavexDbContext CreateDbContext() => new(_options);
    }
}

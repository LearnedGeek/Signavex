using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Signavex.Domain.Interfaces;
using Signavex.Domain.Models;
using Signavex.Infrastructure.Caching;
using Signavex.Infrastructure.Persistence;
using Signavex.Infrastructure.Persistence.Entities;

namespace Signavex.Infrastructure.Tests.Caching;

/// <summary>
/// Verifies the DB-backed cache wrapper around <see cref="IHistoricalOhlcvProvider"/>:
/// cache hits skip the upstream call, misses fetch and persist, and re-fetch
/// upserts (no duplicate rows).
/// </summary>
public class DbCachedHistoricalOhlcvProviderTests : IAsyncDisposable
{
    private readonly IDbContextFactory<SignavexDbContext> _factory;

    public DbCachedHistoricalOhlcvProviderTests()
    {
        var dbName = $"signavex-hist-test-{Guid.NewGuid():N}.db";
        var options = new DbContextOptionsBuilder<SignavexDbContext>()
            .UseSqlite($"Data Source={dbName}")
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureDeletedAsync();
    }

    private DbCachedHistoricalOhlcvProvider CreateCache(Mock<IHistoricalOhlcvProvider> inner) =>
        new(inner.Object, _factory, NullLogger<DbCachedHistoricalOhlcvProvider>.Instance);

    private static OhlcvRecord Bar(string t, DateOnly d, decimal close = 100m) =>
        new(t, d, close, close + 1, close - 1, close, 1_000_000);

    [Fact]
    public async Task ColdCache_FetchesFromInner_AndPersists()
    {
        var inner = new Mock<IHistoricalOhlcvProvider>();
        inner.Setup(i => i.GetHistoricalDailyOhlcvAsync("AAPL", It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OhlcvRecord>
            {
                Bar("AAPL", new DateOnly(2024, 1, 2), 185m),
                Bar("AAPL", new DateOnly(2024, 1, 3), 184m),
            });

        var cache = CreateCache(inner);
        var result = await cache.GetHistoricalDailyOhlcvAsync(
            "AAPL", new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 5));

        Assert.Equal(2, result.Count);
        inner.Verify(i => i.GetHistoricalDailyOhlcvAsync("AAPL", It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Once);

        // Persisted to DB
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await db.HistoricalOhlcv.CountAsync(x => x.Ticker == "AAPL"));
    }

    [Fact]
    public async Task WarmCache_DoesNotCallInner()
    {
        // Pre-seed
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.HistoricalOhlcv.AddRange(
                new HistoricalOhlcvEntity { Ticker = "AAPL", TradingDate = new DateOnly(2024, 1, 2), Open = 185, High = 186, Low = 184, Close = 185, Volume = 1, FetchedAtUtc = DateTime.UtcNow },
                new HistoricalOhlcvEntity { Ticker = "AAPL", TradingDate = new DateOnly(2024, 1, 3), Open = 184, High = 185, Low = 183, Close = 184, Volume = 1, FetchedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var inner = new Mock<IHistoricalOhlcvProvider>(MockBehavior.Strict);
        var cache = CreateCache(inner);

        // Request range whose `to` is within 14d of the latest cached row.
        var result = await cache.GetHistoricalDailyOhlcvAsync(
            "AAPL", new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 10));

        Assert.Equal(2, result.Count);
        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StaleCache_TriggersRefetch()
    {
        // Cached only through 2024-01-03, request goes to 2024-12-01 → way outside tolerance.
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.HistoricalOhlcv.Add(
                new HistoricalOhlcvEntity { Ticker = "AAPL", TradingDate = new DateOnly(2024, 1, 3), Open = 184, High = 185, Low = 183, Close = 184, Volume = 1, FetchedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var inner = new Mock<IHistoricalOhlcvProvider>();
        inner.Setup(i => i.GetHistoricalDailyOhlcvAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OhlcvRecord>
            {
                Bar("AAPL", new DateOnly(2024, 1, 3), 184m),
                Bar("AAPL", new DateOnly(2024, 11, 1), 220m),
            });

        var cache = CreateCache(inner);
        var result = await cache.GetHistoricalDailyOhlcvAsync(
            "AAPL", new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 1));

        inner.Verify(i => i.GetHistoricalDailyOhlcvAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, result.Count);

        // Existing 2024-01-03 row was updated, not duplicated.
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.HistoricalOhlcv.CountAsync(x => x.Ticker == "AAPL"));
    }

    [Fact]
    public async Task InnerReturnsEmpty_FallsBackToCachedRows()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.HistoricalOhlcv.Add(
                new HistoricalOhlcvEntity { Ticker = "AAPL", TradingDate = new DateOnly(2024, 1, 3), Open = 184, High = 185, Low = 183, Close = 184, Volume = 1, FetchedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var inner = new Mock<IHistoricalOhlcvProvider>();
        inner.Setup(i => i.GetHistoricalDailyOhlcvAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<OhlcvRecord>());

        var cache = CreateCache(inner);
        // Stale → triggers fetch → fetch returns empty → fall back to cached row.
        var result = await cache.GetHistoricalDailyOhlcvAsync(
            "AAPL", new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 1));

        Assert.Single(result);
    }

    [Fact]
    public async Task InvertedRange_ReturnsEmptyWithoutHittingDb()
    {
        var inner = new Mock<IHistoricalOhlcvProvider>(MockBehavior.Strict);
        var cache = CreateCache(inner);

        var result = await cache.GetHistoricalDailyOhlcvAsync(
            "AAPL", new DateOnly(2024, 12, 1), new DateOnly(2024, 1, 1));

        Assert.Empty(result);
        inner.VerifyNoOtherCalls();
    }

    private class TestDbContextFactory : IDbContextFactory<SignavexDbContext>
    {
        private readonly DbContextOptions<SignavexDbContext> _options;
        public TestDbContextFactory(DbContextOptions<SignavexDbContext> options) => _options = options;
        public SignavexDbContext CreateDbContext() => new(_options);
    }
}

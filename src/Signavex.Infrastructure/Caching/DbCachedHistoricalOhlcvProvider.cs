using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Signavex.Domain.Interfaces;
using Signavex.Domain.Models;
using Signavex.Infrastructure.Persistence;
using Signavex.Infrastructure.Persistence.Entities;

namespace Signavex.Infrastructure.Caching;

/// <summary>
/// DB-backed cache around an inner <see cref="IHistoricalOhlcvProvider"/>.
/// Past trading days never change once cached (corporate actions are baked
/// into adjusted-close prices), so there is no TTL — coverage is the only
/// freshness check.
///
/// Coverage rule: if the DB already holds a row for this ticker between
/// <c>from</c> and <c>to</c>, AND the most recent cached date is within
/// <c>CoverageTolerance</c> of <c>to</c>, the cached rows are returned
/// without an upstream call. Otherwise the inner provider is hit for the
/// full range and the result is upserted.
/// </summary>
public sealed class DbCachedHistoricalOhlcvProvider : IHistoricalOhlcvProvider
{
    private static readonly TimeSpan CoverageTolerance = TimeSpan.FromDays(14);

    private readonly IHistoricalOhlcvProvider _inner;
    private readonly IDbContextFactory<SignavexDbContext> _dbFactory;
    private readonly ILogger<DbCachedHistoricalOhlcvProvider> _logger;

    public DbCachedHistoricalOhlcvProvider(
        IHistoricalOhlcvProvider inner,
        IDbContextFactory<SignavexDbContext> dbFactory,
        ILogger<DbCachedHistoricalOhlcvProvider> logger)
    {
        _inner = inner;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<OhlcvRecord>> GetHistoricalDailyOhlcvAsync(
        string ticker,
        DateOnly from,
        DateOnly to,
        CancellationToken ct = default)
    {
        if (to < from)
            return Array.Empty<OhlcvRecord>();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var existing = await db.HistoricalOhlcv
            .Where(x => x.Ticker == ticker && x.TradingDate >= from && x.TradingDate <= to)
            .OrderBy(x => x.TradingDate)
            .ToListAsync(ct);

        if (existing.Count > 0 && IsCoverageSufficient(existing, to))
        {
            _logger.LogDebug(
                "Historical OHLCV cache hit for {Ticker} {From}–{To} ({RowCount} rows)",
                ticker, from, to, existing.Count);
            return existing.Select(ToRecord).ToList();
        }

        _logger.LogInformation(
            "Historical OHLCV cache miss/stale for {Ticker} {From}–{To} (have {Have} rows). Fetching from upstream.",
            ticker, from, to, existing.Count);

        var fresh = await _inner.GetHistoricalDailyOhlcvAsync(ticker, from, to, ct);
        if (fresh.Count == 0)
        {
            // Inner failed or returned nothing. Don't poison the cache; serve
            // whatever we already had (may be empty).
            return existing.Select(ToRecord).ToList();
        }

        await UpsertAsync(db, ticker, fresh, ct);
        return fresh;
    }

    private static bool IsCoverageSufficient(IReadOnlyList<HistoricalOhlcvEntity> rows, DateOnly requestedTo)
    {
        var latest = rows[^1].TradingDate;
        var requestedToDt = requestedTo.ToDateTime(TimeOnly.MinValue);
        var latestDt = latest.ToDateTime(TimeOnly.MinValue);
        return (requestedToDt - latestDt) <= CoverageTolerance;
    }

    private static OhlcvRecord ToRecord(HistoricalOhlcvEntity e) =>
        new(e.Ticker, e.TradingDate, e.Open, e.High, e.Low, e.Close, e.Volume);

    private static async Task UpsertAsync(
        SignavexDbContext db,
        string ticker,
        IReadOnlyList<OhlcvRecord> rows,
        CancellationToken ct)
    {
        // Pull the dates we already have for this ticker in the fetched range
        // so we know which rows are inserts vs updates.
        var minDate = rows[0].Date;
        var maxDate = rows[^1].Date;
        var existing = await db.HistoricalOhlcv
            .Where(x => x.Ticker == ticker && x.TradingDate >= minDate && x.TradingDate <= maxDate)
            .ToDictionaryAsync(x => x.TradingDate, ct);

        var fetchedAt = DateTime.UtcNow;
        foreach (var r in rows)
        {
            if (existing.TryGetValue(r.Date, out var entity))
            {
                entity.Open = r.Open;
                entity.High = r.High;
                entity.Low = r.Low;
                entity.Close = r.Close;
                entity.Volume = r.Volume;
                entity.FetchedAtUtc = fetchedAt;
            }
            else
            {
                db.HistoricalOhlcv.Add(new HistoricalOhlcvEntity
                {
                    Ticker = ticker,
                    TradingDate = r.Date,
                    Open = r.Open,
                    High = r.High,
                    Low = r.Low,
                    Close = r.Close,
                    Volume = r.Volume,
                    FetchedAtUtc = fetchedAt,
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }
}

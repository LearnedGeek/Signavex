using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Signavex.Domain.Interfaces;
using Signavex.Engine;
using Signavex.Infrastructure.Persistence;

namespace Signavex.Jobs.Orchestrators;

/// <summary>
/// Drip-feeds Alpha Vantage fundamentals for tickers missing from the cache.
/// Respects the free tier's 25 calls/day limit (12 tickers × 2 calls each,
/// 25s delay between). Extracted from FundamentalsBackfillService.
/// </summary>
public class FundamentalsBackfillOrchestrator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FundamentalsBackfillOrchestrator> _logger;

    private const int MaxCallsPerDay = 24;
    private const int DelayBetweenTickersMs = 25_000;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    public FundamentalsBackfillOrchestrator(
        IServiceScopeFactory scopeFactory,
        ILogger<FundamentalsBackfillOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<int> RunBackfillCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var universeProvider = scope.ServiceProvider.GetRequiredService<UniverseProvider>();
        var fundamentalsProvider = scope.ServiceProvider.GetRequiredService<IFundamentalsProvider>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SignavexDbContext>>();

        var universe = await universeProvider.GetUniverseAsync();
        var allTickers = universe.Select(u => u.Ticker).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var cutoff = DateTime.UtcNow - CacheTtl;
        List<string> cachedTickers;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            cachedTickers = await db.FundamentalsCache
                .Where(f => f.RetrievedAtUtc > cutoff)
                .Select(f => f.Ticker)
                .ToListAsync(ct);
        }

        var cachedSet = new HashSet<string>(cachedTickers, StringComparer.OrdinalIgnoreCase);
        var needsCaching = allTickers.Where(t => !cachedSet.Contains(t)).ToList();

        _logger.LogInformation(
            "Fundamentals backfill: {Cached}/{Total} tickers cached, {Needed} need refresh",
            cachedSet.Count, allTickers.Count, needsCaching.Count);

        if (needsCaching.Count == 0)
            return 0;

        var ticketsThisCycle = needsCaching.Take(MaxCallsPerDay / 2).ToList();
        var filled = 0;

        foreach (var ticker in ticketsThisCycle)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var data = await fundamentalsProvider.GetFundamentalsAsync(ticker);

                if (data.PeRatio.HasValue || data.EpsPreviousYear.HasValue || data.AnalystRating is not null)
                {
                    filled++;
                    _logger.LogDebug("Backfilled fundamentals for {Ticker}", ticker);
                }
                else
                {
                    _logger.LogDebug("No meaningful data returned for {Ticker} (rate limited?)", ticker);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to backfill fundamentals for {Ticker}", ticker);
            }

            await Task.Delay(DelayBetweenTickersMs, ct);
        }

        return filled;
    }
}

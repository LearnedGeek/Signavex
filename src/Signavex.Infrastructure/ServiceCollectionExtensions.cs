using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Signavex.Domain.Configuration;
using Signavex.Domain.Interfaces;
using Signavex.Infrastructure.AlphaVantage;
using Signavex.Infrastructure.Anthropic;
using Signavex.Infrastructure.Caching;
using Signavex.Infrastructure.Fred;
using Signavex.Infrastructure.Persistence;
using Signavex.Infrastructure.Polygon;

namespace Signavex.Infrastructure;

/// <summary>
/// Registers all Signavex infrastructure (data provider) implementations with the DI container.
/// Provider implementations use typed HttpClients for proper lifecycle management.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSignavexInfrastructure(
        this IServiceCollection services,
        DataProviderOptions providerOptions,
        string connectionString)
    {
        services.AddMemoryCache();

        // Database persistence — SQL Server (LocalDB for dev, Azure SQL for production)
        services.AddDbContextFactory<SignavexDbContext>(options =>
            options.UseSqlServer(connectionString));

        services.AddSingleton<IPickOutcomeRecorder, PickOutcomeRecorder>();
        services.AddSingleton<IScanStateStore, SqliteScanStateStore>();
        services.AddSingleton<IScanHistoryStore, SqliteScanHistoryStore>();
        services.AddSingleton<IScanCommandStore, SqliteScanCommandStore>();
        services.AddSingleton<IEconomicDataStore, SqliteEconomicDataStore>();
        services.AddSingleton<IDailyBriefStore, SqliteDailyBriefStore>();

        // Shared rate limiter for all Polygon/Massive API calls.
        // Free tier = 5 req/min. Paid tiers can override via config.
        var polygonRpm = providerOptions.Polygon.MaxRequestsPerMinute > 0
            ? providerOptions.Polygon.MaxRequestsPerMinute
            : 5;

        var polygonLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = polygonRpm,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            TokensPerPeriod = polygonRpm,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 500,
            AutoReplenishment = true,
        });

        services.AddSingleton<RateLimiter>(polygonLimiter);

        services.AddTransient(sp =>
            new PolygonRateLimitingHandler(
                sp.GetRequiredService<RateLimiter>(),
                sp.GetRequiredService<ILogger<PolygonRateLimitingHandler>>()));

        // Polygon market data — register the raw provider (with rate-limit handler)
        // then wrap with in-memory caching. CandidateDetail pages make 3 Polygon
        // calls each; caching eliminates duplicate calls for repeat ticker views
        // within a 15-minute window.
        services.AddHttpClient<PolygonMarketDataProvider>(client =>
        {
            client.BaseAddress = new Uri(providerOptions.Polygon.BaseUrl);
        }).AddHttpMessageHandler<PolygonRateLimitingHandler>();
        services.AddScoped<IMarketDataProvider>(sp =>
            new CachedMarketDataProvider(
                sp.GetRequiredService<PolygonMarketDataProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<CachedMarketDataProvider>()));

        services.AddHttpClient<PolygonNewsProvider>(client =>
        {
            client.BaseAddress = new Uri(providerOptions.Polygon.BaseUrl);
        }).AddHttpMessageHandler<PolygonRateLimitingHandler>();
        services.AddScoped<INewsDataProvider>(sp =>
            new CachedNewsProvider(
                sp.GetRequiredService<PolygonNewsProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<CachedNewsProvider>()));

        // Quantback historical OHLCV — separate cache lane from the live scan
        // path because past trading days never change once cached. The DB
        // decorator queries persisted bars first and only falls through to
        // Polygon on miss/stale, which keeps free-tier rate-limit usage low
        // across repeat backtests.
        services.AddHttpClient<PolygonHistoricalOhlcvProvider>(client =>
        {
            client.BaseAddress = new Uri(providerOptions.Polygon.BaseUrl);
        }).AddHttpMessageHandler<PolygonRateLimitingHandler>();
        services.AddScoped<IHistoricalOhlcvProvider>(sp =>
            new DbCachedHistoricalOhlcvProvider(
                sp.GetRequiredService<PolygonHistoricalOhlcvProvider>(),
                sp.GetRequiredService<IDbContextFactory<SignavexDbContext>>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<DbCachedHistoricalOhlcvProvider>()));

        // Register AlphaVantage as the inner provider, then wrap with DB-backed caching decorator
        services.AddHttpClient<AlphaVantageFundamentalsProvider>(client =>
        {
            client.BaseAddress = new Uri(providerOptions.AlphaVantage.BaseUrl);
        });
        services.AddScoped<IFundamentalsProvider>(sp =>
            new CachedFundamentalsProvider(
                sp.GetRequiredService<AlphaVantageFundamentalsProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                sp.GetRequiredService<IDbContextFactory<SignavexDbContext>>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<CachedFundamentalsProvider>()));

        services.AddHttpClient<IEconomicDataProvider, FredEconomicDataProvider>(client =>
        {
            client.BaseAddress = new Uri(providerOptions.Fred.BaseUrl);
        });

        services.AddHttpClient<IFredApiClient, FredApiClient>(client =>
        {
            client.BaseAddress = new Uri(providerOptions.Fred.BaseUrl);
        });

        services.AddHttpClient<IAiBriefGenerator, AnthropicBriefGenerator>(client =>
        {
            client.BaseAddress = new Uri("https://api.anthropic.com");
            client.Timeout = TimeSpan.FromMinutes(3);
        });

        return services;
    }
}

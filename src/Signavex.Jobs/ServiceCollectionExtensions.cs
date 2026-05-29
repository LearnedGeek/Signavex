using Microsoft.Extensions.DependencyInjection;
using Signavex.Jobs.Orchestrators;

namespace Signavex.Jobs;

/// <summary>
/// Registers the 6 background-job orchestrators as singletons. Used by
/// both the Signavex.Jobs console host (cron entry point) and the
/// Signavex.Web admin endpoints so admins can fire jobs immediately
/// without waiting for the next scheduled cron tick.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSignavexJobs(this IServiceCollection services)
    {
        services.AddSingleton<ScanOrchestrator>();
        services.AddSingleton<BriefOrchestrator>();
        services.AddSingleton<EconomicSyncOrchestrator>();
        services.AddSingleton<FundamentalsBackfillOrchestrator>();
        services.AddSingleton<PickOutcomeEvaluatorOrchestrator>();
        services.AddSingleton<PickOutcomeBackfillOrchestrator>();
        return services;
    }
}

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Signavex.Domain.Configuration;
using Signavex.Engine;
using Signavex.Infrastructure;
using Signavex.Signals;
using Signavex.Functions.Orchestrators;
using Signavex.Functions.Security;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        services
            .AddApplicationInsightsTelemetryWorkerService()
            .ConfigureFunctionsApplicationInsights();

        // Bind configuration options — same pattern as Web and Worker
        services.Configure<SignavexOptions>(
            context.Configuration.GetSection(SignavexOptions.SectionName));

        services.Configure<DataProviderOptions>(
            context.Configuration.GetSection(DataProviderOptions.SectionName));

        services.Configure<AnthropicOptions>(
            context.Configuration.GetSection(AnthropicOptions.SectionName));

        var providerOptions = context.Configuration
            .GetSection(DataProviderOptions.SectionName)
            .Get<DataProviderOptions>() ?? new DataProviderOptions();

        var signavexOptions = context.Configuration
            .GetSection(SignavexOptions.SectionName)
            .Get<SignavexOptions>() ?? new SignavexOptions();

        // Register domain layers (same chain as Web/Worker)
        services
            .AddSignavexSignals()
            .AddSignavexEngine()
            .AddSignavexInfrastructure(providerOptions, signavexOptions.ConnectionString);

        // Orchestrators — self-contained work units, invoked by Function triggers
        services.AddSingleton<ScanOrchestrator>();
        services.AddSingleton<BriefOrchestrator>();
        services.AddSingleton<EconomicSyncOrchestrator>();
        services.AddSingleton<FundamentalsBackfillOrchestrator>();
        services.AddSingleton<PickOutcomeEvaluatorOrchestrator>();

        // Admin authorization — checks x-signavex-admin-key header on HTTP-triggered admin ops
        services.AddSingleton<AdminKeyAuthorizer>();
    })
    .Build();

// NOTE: DB migrations are handled by the Web app on startup. Functions
// do not run MigrateAsync — the schema is guaranteed to exist before any
// function fires because the Web app always boots first (it's the entry
// point for both user traffic and admin HTTP triggers).

await host.RunAsync();

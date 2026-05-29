using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Signavex.Domain.Configuration;
using Signavex.Engine;
using Signavex.Infrastructure;
using Signavex.Jobs;
using Signavex.Jobs.Orchestrators;
using Signavex.Signals;

if (args.Length == 0)
{
    Console.Error.WriteLine(
        "Usage: dotnet Signavex.Jobs.dll <job>\n" +
        "Jobs: scan | scan-resume | brief | sync-economic | fundamentals-backfill | " +
        "evaluate-pick-outcomes | backfill-pick-outcomes");
    return 1;
}

var jobName = args[0];

var builder = Host.CreateApplicationBuilder(args);

// Logging — Serilog to console + rotating file. Hetzner ops tail
// /var/log/signavex/jobs-*.log for visibility.
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console()
    .WriteTo.File(
        path: $"/var/log/signavex/jobs-{jobName}-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog();

// Config sections shared with Web — same SignavexOptions / DataProviderOptions
// / AnthropicOptions binding pattern.
builder.Services.Configure<SignavexOptions>(
    builder.Configuration.GetSection(SignavexOptions.SectionName));
builder.Services.Configure<DataProviderOptions>(
    builder.Configuration.GetSection(DataProviderOptions.SectionName));
builder.Services.Configure<AnthropicOptions>(
    builder.Configuration.GetSection(AnthropicOptions.SectionName));

var providerOptions = builder.Configuration
    .GetSection(DataProviderOptions.SectionName)
    .Get<DataProviderOptions>() ?? new DataProviderOptions();
var signavexOptions = builder.Configuration
    .GetSection(SignavexOptions.SectionName)
    .Get<SignavexOptions>() ?? new SignavexOptions();

builder.Services
    .AddSignavexSignals()
    .AddSignavexEngine()
    .AddSignavexInfrastructure(providerOptions, signavexOptions.ConnectionString)
    .AddSignavexJobs();

using var host = builder.Build();
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
var ct = lifetime.ApplicationStopping;
var logger = host.Services.GetRequiredService<ILogger<Program>>();

try
{
    logger.LogInformation("Signavex.Jobs starting job '{Job}'", jobName);

    var sp = host.Services;

    switch (jobName)
    {
        case "scan":
        case "scan-resume":
            await sp.GetRequiredService<ScanOrchestrator>().RunScanAsync(ct);
            break;
        case "brief":
            await sp.GetRequiredService<BriefOrchestrator>().GenerateBriefAsync(ct);
            break;
        case "sync-economic":
            await sp.GetRequiredService<EconomicSyncOrchestrator>().SyncAllSeriesAsync(ct);
            break;
        case "fundamentals-backfill":
            await sp.GetRequiredService<FundamentalsBackfillOrchestrator>().RunBackfillCycleAsync(ct);
            break;
        case "evaluate-pick-outcomes":
            await sp.GetRequiredService<PickOutcomeEvaluatorOrchestrator>().RunCycleAsync(ct);
            break;
        case "backfill-pick-outcomes":
            await sp.GetRequiredService<PickOutcomeBackfillOrchestrator>().RunAsync(ct);
            break;
        default:
            logger.LogError("Unknown job '{Job}'. See `--help` output for valid names.", jobName);
            return 1;
    }

    logger.LogInformation("Signavex.Jobs job '{Job}' completed.", jobName);
    return 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "Signavex.Jobs job '{Job}' failed.", jobName);
    return 2;
}
finally
{
    await Log.CloseAndFlushAsync();
}

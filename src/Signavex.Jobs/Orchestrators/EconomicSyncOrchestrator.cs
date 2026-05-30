using Microsoft.Extensions.Logging;
using Signavex.Domain.Interfaces;

namespace Signavex.Jobs.Orchestrators;

/// <summary>
/// Pulls the latest observations for all enabled FRED economic series.
/// Extracted from EconomicDataSyncService.SyncAllSeriesAsync — scheduling
/// is handled by the Function Timer trigger.
/// </summary>
public class EconomicSyncOrchestrator
{
    private readonly IFredApiClient _fredClient;
    private readonly IEconomicDataStore _dataStore;
    private readonly ILogger<EconomicSyncOrchestrator> _logger;

    public EconomicSyncOrchestrator(
        IFredApiClient fredClient,
        IEconomicDataStore dataStore,
        ILogger<EconomicSyncOrchestrator> logger)
    {
        _fredClient = fredClient;
        _dataStore = dataStore;
        _logger = logger;
    }

    public async Task SyncAllSeriesAsync(CancellationToken ct)
    {
        var series = await _dataStore.GetAllSeriesAsync(enabledOnly: true, ct);
        _logger.LogInformation("Syncing {Count} enabled economic series", series.Count);

        var synced = 0;
        var errors = 0;

        foreach (var s in series)
        {
            try
            {
                var syncStatus = await _dataStore.GetSyncStatusAsync(s.SeriesId, ct);

                if (syncStatus is not null &&
                    (DateTime.UtcNow - syncStatus.LastSyncUtc).TotalHours < 20)
                {
                    _logger.LogDebug("Skipping {SeriesId} — synced recently", s.SeriesId);
                    continue;
                }

                DateOnly? startDate = syncStatus is not null
                    ? DateOnly.FromDateTime(syncStatus.LastSyncUtc.AddDays(-7))
                    : null;

                var observations = await _fredClient.GetObservationsAsync(s.SeriesId, startDate, ct);

                if (observations.Count > 0)
                {
                    await _dataStore.UpsertObservationsAsync(s.SeriesId, observations, ct);
                }

                var allObs = await _dataStore.GetObservationsAsync(s.SeriesId, ct: ct);
                await _dataStore.UpdateSyncTimestampAsync(s.SeriesId, allObs.Count, ct);

                synced++;
                _logger.LogDebug(
                    "Synced {SeriesId}: {NewCount} observations fetched, {TotalCount} total",
                    s.SeriesId, observations.Count, allObs.Count);
            }
            catch (Exception ex)
            {
                errors++;
                _logger.LogWarning(ex, "Failed to sync {SeriesId}", s.SeriesId);
            }
        }

        _logger.LogInformation(
            "Economic data sync complete: {Synced} synced, {Errors} errors", synced, errors);
    }
}

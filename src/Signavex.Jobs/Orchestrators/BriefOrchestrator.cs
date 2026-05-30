using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Signavex.Domain.Analysis;
using Signavex.Domain.Configuration;
using Signavex.Domain.Interfaces;
using Signavex.Domain.Models;

namespace Signavex.Jobs.Orchestrators;

/// <summary>
/// Generates the daily AI brief end-to-end: builds context from scan + economy
/// data, calls the AI brief generator, persists the result. Extracted from
/// DailyBriefBackgroundService's GenerateBriefAsync + BuildContextAsync methods
/// (scheduling/polling logic is replaced by Function triggers).
/// </summary>
public class BriefOrchestrator
{
    private readonly IScanStateStore _scanStateStore;
    private readonly IScanHistoryStore _scanHistoryStore;
    private readonly IEconomicDataStore _economicDataStore;
    private readonly IDailyBriefStore _briefStore;
    private readonly IAiBriefGenerator _briefGenerator;
    private readonly double _surfacingThreshold;
    private readonly ILogger<BriefOrchestrator> _logger;

    public BriefOrchestrator(
        IScanStateStore scanStateStore,
        IScanHistoryStore scanHistoryStore,
        IEconomicDataStore economicDataStore,
        IDailyBriefStore briefStore,
        IAiBriefGenerator briefGenerator,
        IOptions<SignavexOptions> signavexOptions,
        ILogger<BriefOrchestrator> logger)
    {
        _scanStateStore = scanStateStore;
        _scanHistoryStore = scanHistoryStore;
        _economicDataStore = economicDataStore;
        _briefStore = briefStore;
        _briefGenerator = briefGenerator;
        _surfacingThreshold = signavexOptions.Value.SurfacingThreshold;
        _logger = logger;
    }

    public async Task GenerateBriefAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Starting daily brief generation");
            var context = await BuildContextAsync(ct);

            var (title, content) = await _briefGenerator.GenerateDailyBriefAsync(context, ct);

            var brief = new DailyBrief(
                Id: 0,
                Date: context.Date,
                Title: title,
                Content: content,
                GeneratedAtUtc: DateTime.UtcNow,
                ScanId: context.LatestScan?.ScanId,
                EconomicHealthScore: context.EconomicHealth?.OverallScore,
                MarketOutlook: context.EconomicHealth?.ScoreLabel,
                CandidateCount: context.LatestScan?.Candidates.Count ?? 0);

            await _briefStore.SaveBriefAsync(brief, ct);
            _logger.LogInformation("Daily brief generated and saved: {Title}", title);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate daily brief — retrying once after 60s");

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                var context = await BuildContextAsync(ct);
                var (title, content) = await _briefGenerator.GenerateDailyBriefAsync(context, ct);

                var brief = new DailyBrief(
                    Id: 0,
                    Date: context.Date,
                    Title: title,
                    Content: content,
                    GeneratedAtUtc: DateTime.UtcNow,
                    ScanId: context.LatestScan?.ScanId,
                    EconomicHealthScore: context.EconomicHealth?.OverallScore,
                    MarketOutlook: context.EconomicHealth?.ScoreLabel,
                    CandidateCount: context.LatestScan?.Candidates.Count ?? 0);

                await _briefStore.SaveBriefAsync(brief, ct);
                _logger.LogInformation("Daily brief generated on retry: {Title}", title);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception retryEx)
            {
                _logger.LogError(retryEx, "Daily brief generation failed on retry — skipping today");
            }
        }
    }

    private async Task<DailyBriefContext> BuildContextAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Filter candidates by surfacing threshold — see DailyBriefBackgroundService
        // for the full reasoning (fixes the "10 solid stocks on a 0-pick day" bug).
        var latestScan = await _scanStateStore.LoadLatestResultAsync(ct);
        if (latestScan is not null)
        {
            var surfaced = latestScan.Candidates
                .Where(c => c.FinalScore >= _surfacingThreshold)
                .ToList()
                .AsReadOnly();
            latestScan = latestScan with { Candidates = surfaced };
        }

        var recentScans = await _scanHistoryStore.GetRecentScansAsync(30, ct);
        var multiplierTrend = await _scanHistoryStore.GetMarketMultiplierTrendAsync(30, ct);

        var allSeries = await _economicDataStore.GetAllSeriesAsync(enabledOnly: true, ct);
        var analysisService = new EconomicAnalysisService();
        var interpretations = new List<IndicatorInterpretation>();

        foreach (var series in allSeries)
        {
            var observations = await _economicDataStore.GetObservationsAsync(series.SeriesId, ct: ct);
            if (observations.Count < 2) continue;

            var interp = analysisService.AnalyzeIndicator(series, observations);
            interpretations.Add(interp);
        }

        var health = interpretations.Count > 0
            ? analysisService.CalculateEconomicHealth(interpretations)
            : null;

        var correlationService = new CorrelationAnalysisService();
        var correlations = interpretations.Count > 0
            ? correlationService.Analyze(interpretations)
            : null;

        ActionPlan? actionPlan = null;
        if (interpretations.Count > 0 && correlations is not null)
        {
            var recService = new RecommendationService();
            actionPlan = recService.GetRecommendations(
                UserProfile.General, interpretations, correlations);
        }

        return new DailyBriefContext(
            LatestScan: latestScan,
            RecentScans: recentScans,
            MultiplierTrend: multiplierTrend,
            EconomicHealth: health,
            Indicators: interpretations.AsReadOnly(),
            Correlations: correlations,
            ActionPlan: actionPlan,
            Date: today);
    }
}

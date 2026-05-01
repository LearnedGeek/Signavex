using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Signavex.Domain.Configuration;
using Signavex.Domain.Enums;
using Signavex.Domain.Interfaces;
using Signavex.Domain.Models;
using Signavex.Infrastructure.Persistence.Entities;

namespace Signavex.Infrastructure.Persistence;

public class SqliteScanStateStore : IScanStateStore
{
    private readonly IDbContextFactory<SignavexDbContext> _dbFactory;
    private readonly ILogger<SqliteScanStateStore> _logger;
    private readonly double _surfacingThreshold;
    private readonly IPickOutcomeRecorder _pickOutcomeRecorder;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public SqliteScanStateStore(
        IDbContextFactory<SignavexDbContext> dbFactory,
        ILogger<SqliteScanStateStore> logger,
        IOptions<SignavexOptions> options,
        IPickOutcomeRecorder pickOutcomeRecorder)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _surfacingThreshold = options.Value.SurfacingThreshold;
        _pickOutcomeRecorder = pickOutcomeRecorder;
    }

    public Task SaveCheckpointAsync(ScanCheckpoint checkpoint, CancellationToken ct = default)
        => SaveCheckpointAsync(checkpoint, checkpoint.UniverseTickers.Count, ct);

    public async Task SaveCheckpointAsync(ScanCheckpoint checkpoint, int totalOverride, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var payload = JsonSerializer.Serialize(checkpoint, JsonOptions);
        var total = totalOverride > 0 ? totalOverride : checkpoint.UniverseTickers.Count;
        var existing = await db.ScanCheckpoints.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            existing.ScanId = checkpoint.ScanId;
            existing.StartedAtUtc = checkpoint.StartedAtUtc;
            existing.PayloadJson = payload;
            existing.UpdatedAtUtc = DateTime.UtcNow;
            existing.Evaluated = checkpoint.EvaluatedTickers.Count;
            existing.Total = total;
            existing.CurrentTicker = checkpoint.EvaluatedTickers.Count > 0
                ? checkpoint.EvaluatedTickers[^1] : string.Empty;
            existing.CandidatesFound = checkpoint.CandidatesSoFar.Count(c => c.FinalScore >= _surfacingThreshold);
            existing.ErrorCount = checkpoint.ErrorCount;
            existing.IsActive = true;
        }
        else
        {
            db.ScanCheckpoints.Add(new ScanCheckpointEntity
            {
                ScanId = checkpoint.ScanId,
                StartedAtUtc = checkpoint.StartedAtUtc,
                PayloadJson = payload,
                UpdatedAtUtc = DateTime.UtcNow,
                Evaluated = checkpoint.EvaluatedTickers.Count,
                Total = total,
                CurrentTicker = checkpoint.EvaluatedTickers.Count > 0
                    ? checkpoint.EvaluatedTickers[^1] : string.Empty,
                CandidatesFound = checkpoint.CandidatesSoFar.Count(c => c.FinalScore >= _surfacingThreshold),
                ErrorCount = checkpoint.ErrorCount,
                IsActive = true
            });
        }

        await db.SaveChangesAsync(ct);
        _logger.LogDebug("Saved checkpoint: {Evaluated}/{Total} evaluated",
            checkpoint.EvaluatedTickers.Count, total);
    }

    public async Task<ScanCheckpoint?> LoadCheckpointAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ScanCheckpoints.AsNoTracking().OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefaultAsync(ct);

        if (entity is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<ScanCheckpoint>(entity.PayloadJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize checkpoint — data may be corrupted");
            return null;
        }
    }

    public async Task DeleteCheckpointAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.ScanCheckpoints.ExecuteDeleteAsync(ct);
        _logger.LogDebug("Deleted checkpoint");
    }

    public async Task SaveCompletedResultAsync(CompletedScanResult result, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var run = new ScanRunEntity
        {
            ScanId = result.ScanId,
            CompletedAtUtc = result.CompletedAtUtc,
            MarketMultiplier = result.MarketContext.Multiplier,
            MarketSummary = result.MarketContext.Summary,
            MarketSignalsJson = JsonSerializer.Serialize(result.MarketContext.MarketSignals, JsonOptions),
            TotalEvaluated = result.TotalEvaluated,
            ErrorCount = result.ErrorCount,
            CandidateCount = result.Candidates.Count(c => c.FinalScore >= _surfacingThreshold),
            Candidates = result.Candidates.Select(c => new ScanCandidateEntity
            {
                Ticker = c.Ticker,
                CompanyName = c.CompanyName,
                Tier = (int)c.Tier,
                RawScore = c.RawScore,
                FinalScore = c.FinalScore,
                EvaluatedAt = c.EvaluatedAt,
                SignalResultsJson = JsonSerializer.Serialize(c.SignalResults, JsonOptions)
            }).ToList()
        };

        db.ScanRuns.Add(run);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Saved completed scan result: {Count} candidates", result.Candidates.Count);

        // FT1: forward-test capture. Persisting the pick outcomes after the
        // scan run is saved keeps the two writes loosely coupled — if the
        // recorder ever throws, the scan run itself is still saved.
        try
        {
            var scanDate = DateOnly.FromDateTime(result.CompletedAtUtc);
            await _pickOutcomeRecorder.RecordScanAsync(scanDate, result.Candidates, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record pick outcomes for scan {ScanId} — continuing", result.ScanId);
        }
    }

    public async Task<CompletedScanResult?> LoadLatestResultAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var run = await db.ScanRuns
            .AsNoTracking()
            .Include(r => r.Candidates)
            .OrderByDescending(r => r.CompletedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (run is null)
            return null;

        return ToCompletedScanResult(run);
    }

    internal static CompletedScanResult ToCompletedScanResult(ScanRunEntity run)
    {
        var marketSignals = DeserializeSignals(run.MarketSignalsJson);
        var marketContext = new MarketContext(run.MarketMultiplier, run.MarketSummary, marketSignals);

        var candidates = run.Candidates
            .Select(c => new StockCandidate(
                c.Ticker,
                c.CompanyName,
                (MarketTier)c.Tier,
                c.RawScore,
                c.FinalScore,
                DeserializeSignals(c.SignalResultsJson),
                marketContext,
                c.EvaluatedAt))
            .OrderByDescending(c => c.FinalScore)
            .ToList()
            .AsReadOnly();

        return new CompletedScanResult(
            run.ScanId, run.CompletedAtUtc, marketContext,
            candidates, run.TotalEvaluated, run.ErrorCount);
    }

    public async Task<ScanStatus> GetScanStatusAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ScanCheckpoints
            .AsNoTracking()
            .OrderByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (entity is null || !entity.IsActive)
        {
            return new ScanStatus(
                IsScanning: false,
                Evaluated: 0,
                Total: 0,
                CurrentTicker: string.Empty,
                CandidatesFound: 0,
                ErrorCount: 0,
                StartedAtUtc: null,
                LastUpdatedAtUtc: null);
        }

        return new ScanStatus(
            IsScanning: true,
            Evaluated: entity.Evaluated,
            Total: entity.Total,
            CurrentTicker: entity.CurrentTicker,
            CandidatesFound: entity.CandidatesFound,
            ErrorCount: entity.ErrorCount,
            StartedAtUtc: entity.StartedAtUtc,
            LastUpdatedAtUtc: entity.UpdatedAtUtc);
    }

    public async Task SetCheckpointInactiveAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ScanCheckpoints.OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefaultAsync(ct);
        if (entity is not null)
        {
            entity.IsActive = false;
            await db.SaveChangesAsync(ct);
        }
    }

    internal static IReadOnlyList<SignalResult> DeserializeSignals(string json)
    {
        if (string.IsNullOrEmpty(json))
            return Array.Empty<SignalResult>();

        try
        {
            return JsonSerializer.Deserialize<List<SignalResult>>(json, JsonOptions)
                   ?? (IReadOnlyList<SignalResult>)Array.Empty<SignalResult>();
        }
        catch (JsonException)
        {
            return Array.Empty<SignalResult>();
        }
    }
}

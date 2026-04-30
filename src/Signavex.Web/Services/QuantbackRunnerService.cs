using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Signavex.Domain.Interfaces;
using Signavex.Domain.Models.Portfolio;
using Signavex.Infrastructure.Persistence;
using Signavex.Infrastructure.Persistence.Entities;

namespace Signavex.Web.Services;

/// <summary>
/// User-scoped, DB-backed runner. Each Quantback execution is a row in
/// <c>QuantbackRuns</c> tagged with the user that started it. Results
/// survive App Service sleeps, restarts, and deploys; users come back
/// to <c>/quantback</c> later and see their own latest run.
///
/// Concurrency rule: at most one in-flight run per user. Calls to
/// <see cref="TryStartRunAsync"/> while a Running row exists for that
/// user return false (the page surfaces the existing one).
/// </summary>
public class QuantbackRunnerService
{
    private const string StatusRunning = "Running";
    private const string StatusComplete = "Complete";
    private const string StatusFailed = "Failed";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // STJ in net8 handles DateOnly + records out of the box.
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDbContextFactory<SignavexDbContext> _dbFactory;
    private readonly ILogger<QuantbackRunnerService> _logger;

    public QuantbackRunnerService(
        IServiceScopeFactory scopeFactory,
        IDbContextFactory<SignavexDbContext> dbFactory,
        ILogger<QuantbackRunnerService> logger)
    {
        _scopeFactory = scopeFactory;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>
    /// Latest run for a user, regardless of status. Used by the page to
    /// render either the in-flight banner or the completed result.
    /// </summary>
    public async Task<QuantbackRunSummary?> GetLatestRunAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.QuantbackRuns
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.StartedAtUtc)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : Hydrate(entity);
    }

    /// <summary>
    /// Kicks off a backtest in the background tagged with <paramref name="userId"/>.
    /// Returns false if this user already has a Running row (rate-limit:
    /// one in-flight per account).
    /// </summary>
    public async Task<bool> TryStartRunAsync(string userId, PortfolioBacktestRequest request, CancellationToken ct = default)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var alreadyRunning = await db.QuantbackRuns
                .AnyAsync(x => x.UserId == userId && x.Status == StatusRunning, ct);
            if (alreadyRunning)
            {
                _logger.LogInformation("Quantback start refused for {UserId} — a run is already in flight.", userId);
                return false;
            }

            var row = new QuantbackRunEntity
            {
                UserId = userId,
                Status = StatusRunning,
                StartedAtUtc = DateTime.UtcNow,
                RequestJson = JsonSerializer.Serialize(request, JsonOptions),
            };
            db.QuantbackRuns.Add(row);
            await db.SaveChangesAsync(ct);

            // Fire-and-forget. The background task uses its own scope and
            // updates the row on completion. We deliberately don't await so
            // the form POST returns quickly.
            _ = Task.Run(() => RunBackgroundAsync(row.Id, request, userId));
        }
        return true;
    }

    private async Task RunBackgroundAsync(int runId, PortfolioBacktestRequest request, string userId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var backtester = scope.ServiceProvider.GetRequiredService<IPortfolioBacktester>();
            var result = await backtester.RunAsync(request);

            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.QuantbackRuns.FindAsync(runId);
            if (row is null) return;
            row.Status = StatusComplete;
            row.CompletedAtUtc = DateTime.UtcNow;
            row.ResultJson = JsonSerializer.Serialize(result, JsonOptions);
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Quantback complete for {UserId} (run {RunId}): {TradeCount} trades, total return {Return:P2}",
                userId, runId, result.Trades.Count, result.Metrics.TotalReturnPct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quantback failed for {UserId} (run {RunId})", userId, runId);
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var row = await db.QuantbackRuns.FindAsync(runId);
                if (row is not null)
                {
                    row.Status = StatusFailed;
                    row.CompletedAtUtc = DateTime.UtcNow;
                    row.Error = ex.Message;
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception inner)
            {
                _logger.LogError(inner, "Failed to record Quantback failure for run {RunId}", runId);
            }
        }
    }

    private static QuantbackRunSummary Hydrate(QuantbackRunEntity entity)
    {
        PortfolioBacktestRequest? request = null;
        try { request = JsonSerializer.Deserialize<PortfolioBacktestRequest>(entity.RequestJson, JsonOptions); }
        catch { /* malformed; treat as null */ }

        PortfolioBacktestResult? result = null;
        if (entity.Status == StatusComplete && !string.IsNullOrEmpty(entity.ResultJson))
        {
            try { result = JsonSerializer.Deserialize<PortfolioBacktestResult>(entity.ResultJson, JsonOptions); }
            catch { /* malformed; treat as null */ }
        }

        return new QuantbackRunSummary(
            Id: entity.Id,
            Status: entity.Status,
            StartedAtUtc: entity.StartedAtUtc,
            CompletedAtUtc: entity.CompletedAtUtc,
            Request: request,
            Result: result,
            Error: entity.Error);
    }
}

/// <summary>UI-facing summary of a single Quantback run row.</summary>
public record QuantbackRunSummary(
    int Id,
    string Status,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    PortfolioBacktestRequest? Request,
    PortfolioBacktestResult? Result,
    string? Error
)
{
    public bool IsRunning => Status == "Running";
    public bool IsComplete => Status == "Complete";
    public bool IsFailed => Status == "Failed";
}

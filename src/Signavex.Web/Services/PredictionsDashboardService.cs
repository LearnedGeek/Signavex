using Microsoft.EntityFrameworkCore;
using Signavex.Infrastructure.Persistence;
using Signavex.Infrastructure.Persistence.Entities;

namespace Signavex.Web.Services;

/// <summary>
/// Reads <c>PickOutcomes</c> and computes aggregates for the FT4
/// dashboard: hit rate (% beating SPY), average outperformance per
/// horizon, score-bucket "is the score predictive" analysis, and the
/// per-pick row list.
/// </summary>
public class PredictionsDashboardService
{
    private readonly IDbContextFactory<SignavexDbContext> _dbFactory;

    public PredictionsDashboardService(IDbContextFactory<SignavexDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<PredictionsDashboard> GetDashboardAsync(string? tickerFilter = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var query = db.PickOutcomes.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(tickerFilter))
        {
            var ticker = tickerFilter.Trim().ToUpperInvariant();
            query = query.Where(p => p.Ticker == ticker);
        }

        var rows = await query
            .OrderByDescending(p => p.ScanDate)
            .ToListAsync(ct);

        return new PredictionsDashboard(
            TotalRows: rows.Count,
            EvaluatedRows: rows.Count(p => p.EntryDate is not null),
            TickerFilter: tickerFilter,
            HorizonStats: BuildHorizonStats(rows),
            ScoreBuckets: BuildScoreBuckets(rows),
            RecentPicks: rows.Take(200).Select(MapToRow).ToList());
    }

    /// <summary>
    /// Streams all rows (respecting the optional ticker filter) as a CSV
    /// string. Used by the FT5 export endpoint.
    /// </summary>
    public async Task<string> ExportCsvAsync(string? tickerFilter = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.PickOutcomes.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(tickerFilter))
        {
            var ticker = tickerFilter.Trim().ToUpperInvariant();
            query = query.Where(p => p.Ticker == ticker);
        }

        var rows = await query.OrderByDescending(p => p.ScanDate).ToListAsync(ct);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("ScanDate,Ticker,FinalScore,EntryDate,EntryPrice,SpyEntryPrice," +
                      "TickerReturn30d,SpyReturn30d,Outperformance30d," +
                      "TickerReturn90d,SpyReturn90d,Outperformance90d," +
                      "TickerReturn180d,SpyReturn180d,Outperformance180d," +
                      "TickerReturn365d,SpyReturn365d,Outperformance365d");

        foreach (var r in rows)
        {
            sb.Append(r.ScanDate.ToString("yyyy-MM-dd")).Append(',');
            sb.Append(r.Ticker).Append(',');
            sb.Append(r.FinalScore.ToString("F4")).Append(',');
            sb.Append(r.EntryDate?.ToString("yyyy-MM-dd") ?? "").Append(',');
            sb.Append(r.EntryPrice?.ToString("F4") ?? "").Append(',');
            sb.Append(r.SpyEntryPrice?.ToString("F4") ?? "").Append(',');
            sb.Append(r.TickerReturn30d?.ToString("F6") ?? "").Append(',');
            sb.Append(r.SpyReturn30d?.ToString("F6") ?? "").Append(',');
            sb.Append(r.Outperformance30d?.ToString("F6") ?? "").Append(',');
            sb.Append(r.TickerReturn90d?.ToString("F6") ?? "").Append(',');
            sb.Append(r.SpyReturn90d?.ToString("F6") ?? "").Append(',');
            sb.Append(r.Outperformance90d?.ToString("F6") ?? "").Append(',');
            sb.Append(r.TickerReturn180d?.ToString("F6") ?? "").Append(',');
            sb.Append(r.SpyReturn180d?.ToString("F6") ?? "").Append(',');
            sb.Append(r.Outperformance180d?.ToString("F6") ?? "").Append(',');
            sb.Append(r.TickerReturn365d?.ToString("F6") ?? "").Append(',');
            sb.Append(r.SpyReturn365d?.ToString("F6") ?? "").Append(',');
            sb.AppendLine(r.Outperformance365d?.ToString("F6") ?? "");
        }

        return sb.ToString();
    }

    private static IReadOnlyList<HorizonStat> BuildHorizonStats(IReadOnlyList<PickOutcomeEntity> rows)
    {
        return new[]
        {
            BuildOne("30d", rows, p => p.TickerReturn30d, p => p.SpyReturn30d, p => p.Outperformance30d),
            BuildOne("90d", rows, p => p.TickerReturn90d, p => p.SpyReturn90d, p => p.Outperformance90d),
            BuildOne("180d", rows, p => p.TickerReturn180d, p => p.SpyReturn180d, p => p.Outperformance180d),
            BuildOne("365d", rows, p => p.TickerReturn365d, p => p.SpyReturn365d, p => p.Outperformance365d),
        };
    }

    private static HorizonStat BuildOne(
        string label,
        IReadOnlyList<PickOutcomeEntity> rows,
        Func<PickOutcomeEntity, double?> tickerReturn,
        Func<PickOutcomeEntity, double?> spyReturn,
        Func<PickOutcomeEntity, double?> outperformance)
    {
        var graded = rows.Where(p => outperformance(p) is not null).ToList();
        if (graded.Count == 0)
            return new HorizonStat(label, 0, 0, 0, 0, 0);

        return new HorizonStat(
            Label: label,
            EvaluatedCount: graded.Count,
            AvgTickerReturn: graded.Average(p => tickerReturn(p) ?? 0),
            AvgSpyReturn: graded.Average(p => spyReturn(p) ?? 0),
            AvgOutperformance: graded.Average(p => outperformance(p) ?? 0),
            HitRate: (double)graded.Count(p => (outperformance(p) ?? 0) > 0) / graded.Count);
    }

    private static IReadOnlyList<ScoreBucket> BuildScoreBuckets(IReadOnlyList<PickOutcomeEntity> rows)
    {
        // Bucket by FinalScore — answers "do high-scoring picks actually
        // outperform low-scoring picks?". Uses the 90-day horizon as the
        // reference window since it's enough for noise to wash out without
        // requiring 6+ months of cohort age.
        var buckets = new (double min, double max, string label)[]
        {
            (-1.0, 0.0,  "≤ 0"),
            ( 0.0, 0.3,  "0–0.3"),
            ( 0.3, 0.5,  "0.3–0.5"),
            ( 0.5, 0.7,  "0.5–0.7"),
            ( 0.7, 1.01, "≥ 0.7"),
        };

        var result = new List<ScoreBucket>();
        foreach (var (min, max, label) in buckets)
        {
            var inBucket = rows.Where(p =>
                p.Outperformance90d is not null &&
                p.FinalScore >= min && p.FinalScore < max).ToList();

            result.Add(new ScoreBucket(
                Label: label,
                Count: inBucket.Count,
                AvgOutperformance90d: inBucket.Count == 0 ? 0 : inBucket.Average(p => p.Outperformance90d ?? 0),
                HitRate90d: inBucket.Count == 0 ? 0 : (double)inBucket.Count(p => (p.Outperformance90d ?? 0) > 0) / inBucket.Count));
        }
        return result;
    }

    private static PickRow MapToRow(PickOutcomeEntity e) => new(
        ScanDate: e.ScanDate,
        Ticker: e.Ticker,
        FinalScore: e.FinalScore,
        EntryDate: e.EntryDate,
        EntryPrice: e.EntryPrice,
        TickerReturn30d: e.TickerReturn30d,
        Outperformance30d: e.Outperformance30d,
        TickerReturn90d: e.TickerReturn90d,
        Outperformance90d: e.Outperformance90d,
        TickerReturn180d: e.TickerReturn180d,
        Outperformance180d: e.Outperformance180d,
        TickerReturn365d: e.TickerReturn365d,
        Outperformance365d: e.Outperformance365d);
}

public record PredictionsDashboard(
    int TotalRows,
    int EvaluatedRows,
    string? TickerFilter,
    IReadOnlyList<HorizonStat> HorizonStats,
    IReadOnlyList<ScoreBucket> ScoreBuckets,
    IReadOnlyList<PickRow> RecentPicks);

public record HorizonStat(
    string Label,
    int EvaluatedCount,
    double AvgTickerReturn,
    double AvgSpyReturn,
    double AvgOutperformance,
    double HitRate);

public record ScoreBucket(
    string Label,
    int Count,
    double AvgOutperformance90d,
    double HitRate90d);

public record PickRow(
    DateOnly ScanDate,
    string Ticker,
    double FinalScore,
    DateOnly? EntryDate,
    decimal? EntryPrice,
    double? TickerReturn30d,
    double? Outperformance30d,
    double? TickerReturn90d,
    double? Outperformance90d,
    double? TickerReturn180d,
    double? Outperformance180d,
    double? TickerReturn365d,
    double? Outperformance365d);

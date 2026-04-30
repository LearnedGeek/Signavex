using Signavex.Domain.Models.Portfolio;

namespace Signavex.Engine.Portfolio;

/// <summary>
/// Computes summary statistics from a completed simulation's trade log
/// and equity curve. Pure functions — no DI dependencies, fully testable
/// against canned inputs.
/// </summary>
public static class PortfolioMetricsCalculator
{
    private const int TradingDaysPerYear = 252;

    public static PortfolioBacktestMetrics ComputeMetrics(
        decimal startingCapital,
        IReadOnlyList<Trade> trades,
        IReadOnlyList<EquityPoint> equityCurve)
    {
        if (equityCurve.Count == 0)
            return PortfolioBacktestMetrics.Empty(startingCapital);

        var endingEquity = equityCurve[^1].TotalEquity;
        var totalReturn = startingCapital == 0
            ? 0.0
            : (double)((endingEquity - startingCapital) / startingCapital);

        var annualizedReturn = ComputeAnnualizedReturn(equityCurve, totalReturn);
        var sharpe = ComputeSharpe(equityCurve);
        var maxDrawdown = ComputeMaxDrawdown(equityCurve);

        var winners = trades.Where(t => t.RealizedPnL > 0).ToList();
        var losers = trades.Where(t => t.RealizedPnL < 0).ToList();
        var avgHoldDays = trades.Count == 0 ? 0.0 : trades.Average(t => (double)t.HoldDays);
        var winRate = trades.Count == 0 ? 0.0 : (double)winners.Count / trades.Count;

        return new PortfolioBacktestMetrics(
            StartingEquity: startingCapital,
            EndingEquity: endingEquity,
            TotalReturnPct: totalReturn,
            AnnualizedReturnPct: annualizedReturn,
            SharpeRatio: sharpe,
            MaxDrawdownPct: maxDrawdown,
            TotalTrades: trades.Count,
            WinningTrades: winners.Count,
            LosingTrades: losers.Count,
            WinRate: winRate,
            AvgWinPnL: winners.Count == 0 ? 0m : winners.Average(t => t.RealizedPnL),
            AvgLossPnL: losers.Count == 0 ? 0m : losers.Average(t => t.RealizedPnL),
            AvgHoldDays: avgHoldDays);
    }

    /// <summary>
    /// Compound annualized return from total return and elapsed days.
    /// (1 + total)^(365 / days) - 1.
    /// </summary>
    public static double ComputeAnnualizedReturn(IReadOnlyList<EquityPoint> equityCurve, double totalReturn)
    {
        if (equityCurve.Count < 2) return 0.0;
        var first = equityCurve[0].Date;
        var last = equityCurve[^1].Date;
        var days = (last.ToDateTime(TimeOnly.MinValue) - first.ToDateTime(TimeOnly.MinValue)).TotalDays;
        if (days <= 0) return 0.0;

        return Math.Pow(1.0 + totalReturn, 365.0 / days) - 1.0;
    }

    /// <summary>
    /// Annualized Sharpe ratio. Computes period-over-period equity returns,
    /// subtracts a 0% risk-free rate (configurable via Q7's StrategyParameters
    /// in a follow-up), and scales by sqrt(252) for annualization. Returns 0
    /// when stddev is zero or there are too few data points.
    /// </summary>
    public static double ComputeSharpe(IReadOnlyList<EquityPoint> equityCurve)
    {
        if (equityCurve.Count < 2) return 0.0;

        var returns = new List<double>(equityCurve.Count - 1);
        for (int i = 1; i < equityCurve.Count; i++)
        {
            var prev = (double)equityCurve[i - 1].TotalEquity;
            var curr = (double)equityCurve[i].TotalEquity;
            if (prev <= 0) continue;
            returns.Add((curr - prev) / prev);
        }
        if (returns.Count < 2) return 0.0;

        var mean = returns.Average();
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
        var stddev = Math.Sqrt(variance);
        if (stddev == 0) return 0.0;

        return (mean / stddev) * Math.Sqrt(TradingDaysPerYear);
    }

    /// <summary>
    /// Maximum peak-to-trough drawdown across the equity curve, expressed
    /// as a positive fraction (e.g., 0.15 means a 15% drawdown).
    /// </summary>
    public static double ComputeMaxDrawdown(IReadOnlyList<EquityPoint> equityCurve)
    {
        if (equityCurve.Count == 0) return 0.0;

        decimal peak = equityCurve[0].TotalEquity;
        decimal maxDrop = 0;

        foreach (var point in equityCurve)
        {
            if (point.TotalEquity > peak)
                peak = point.TotalEquity;
            else
            {
                var drop = peak - point.TotalEquity;
                if (drop > maxDrop) maxDrop = drop;
            }
        }

        if (peak == 0) return 0.0;
        // Express as fraction of the peak that triggered the drop. We re-find
        // it by walking again so the peak we divide by matches the peak that
        // led to maxDrop, not the current rolling peak at end.
        decimal worstPeak = equityCurve[0].TotalEquity;
        decimal rollingPeak = worstPeak;
        decimal worstDrop = 0;
        foreach (var point in equityCurve)
        {
            if (point.TotalEquity > rollingPeak)
                rollingPeak = point.TotalEquity;
            var drop = rollingPeak - point.TotalEquity;
            if (drop > worstDrop)
            {
                worstDrop = drop;
                worstPeak = rollingPeak;
            }
        }
        return worstPeak == 0 ? 0.0 : (double)(worstDrop / worstPeak);
    }

    /// <summary>
    /// Realized P&amp;L grouped by exit-month. Months with no trades are omitted.
    /// </summary>
    public static IReadOnlyList<MonthlyPnLPoint> ComputeMonthlyPnL(IReadOnlyList<Trade> trades)
    {
        return trades
            .GroupBy(t => new { t.ExitDate.Year, t.ExitDate.Month })
            .Select(g => new MonthlyPnLPoint(
                Year: g.Key.Year,
                Month: g.Key.Month,
                RealizedPnL: g.Sum(t => t.RealizedPnL),
                TradeCount: g.Count()))
            .OrderBy(p => p.Year).ThenBy(p => p.Month)
            .ToList();
    }

    /// <summary>
    /// Per-ticker breakdown — TotalPnL, win count, avg hold days. Useful for
    /// identifying the names that drove the result.
    /// </summary>
    public static IReadOnlyList<TickerStats> ComputePerTickerBreakdown(IReadOnlyList<Trade> trades)
    {
        return trades
            .GroupBy(t => t.Ticker)
            .Select(g =>
            {
                var list = g.ToList();
                var winners = list.Count(t => t.RealizedPnL > 0);
                return new TickerStats(
                    Ticker: g.Key,
                    TradeCount: list.Count,
                    WinningTrades: winners,
                    TotalPnL: list.Sum(t => t.RealizedPnL),
                    WinRate: list.Count == 0 ? 0.0 : (double)winners / list.Count,
                    AvgHoldDays: list.Count == 0 ? 0.0 : list.Average(t => (double)t.HoldDays));
            })
            .OrderByDescending(s => s.TotalPnL)
            .ToList();
    }
}

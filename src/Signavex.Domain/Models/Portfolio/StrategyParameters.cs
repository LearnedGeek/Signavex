namespace Signavex.Domain.Models.Portfolio;

/// <summary>
/// Mechanical-strategy rules applied each simulated trading day.
/// All percentage fields are unit-fractions (0.05 = 5%), matching how
/// <see cref="SignalResult"/> scores are expressed.
///
/// Q7 realism fields (<see cref="SlippageBps"/>, <see cref="CommissionPerTrade"/>,
/// <see cref="RiskFreeAnnualRate"/>) default to zero so legacy callers see no
/// behavioral change unless they opt in.
/// </summary>
public record StrategyParameters(
    decimal PositionSizePct,
    decimal MaxPerTickerPct,
    decimal StopLossPct,
    decimal TakeProfitPct,
    bool ExitOnSignalReversal,
    double MinScoreToEnter,
    double SlippageBps = 0,
    decimal CommissionPerTrade = 0m,
    double RiskFreeAnnualRate = 0
)
{
    public static StrategyParameters Default => new(
        PositionSizePct: 0.05m,
        MaxPerTickerPct: 0.20m,
        StopLossPct: 0.08m,
        TakeProfitPct: 0.20m,
        ExitOnSignalReversal: true,
        MinScoreToEnter: 0.45,
        SlippageBps: 0,
        CommissionPerTrade: 0m,
        RiskFreeAnnualRate: 0);

    /// <summary>
    /// Realistic defaults for a retail strategy: 5 bps slippage per side
    /// (e.g., wider spread on small-cap entries), $1 per trade commission,
    /// 4% short-term Treasury rate for cash drag.
    /// </summary>
    public static StrategyParameters Realistic => Default with
    {
        SlippageBps = 5,
        CommissionPerTrade = 1m,
        RiskFreeAnnualRate = 0.04,
    };
}

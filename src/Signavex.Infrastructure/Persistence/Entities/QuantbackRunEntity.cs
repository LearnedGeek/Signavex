namespace Signavex.Infrastructure.Persistence.Entities;

/// <summary>
/// One row per Quantback backtest run, scoped to a user. Stores the full
/// request and result as JSON so the run survives App Service restarts and
/// the user can come back hours later to see their own latest result.
///
/// Status transitions: <c>Running</c> → <c>Complete</c> | <c>Failed</c>.
/// </summary>
public class QuantbackRunEntity
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Status { get; set; } = "Running";

    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Serialized <c>PortfolioBacktestRequest</c>.</summary>
    public string RequestJson { get; set; } = string.Empty;

    /// <summary>Serialized <c>PortfolioBacktestResult</c>; null until Status = Complete.</summary>
    public string? ResultJson { get; set; }

    /// <summary>Captured exception message when Status = Failed.</summary>
    public string? Error { get; set; }
}

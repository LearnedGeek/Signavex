using Signavex.Domain.Models;

namespace Signavex.Domain.Interfaces;

/// <summary>
/// Captures every scored candidate from a daily scan into the
/// <c>PickOutcomes</c> table for forward-test analytics. Idempotent — a
/// repeat call for the same <paramref name="scanDate"/> + ticker is a
/// no-op (enforced by the unique index on the underlying table).
///
/// Entry price resolution (next trading day's close) and per-horizon
/// price lookups are performed later by the nightly evaluator job (FT2),
/// not here — at scan time the next-day close hasn't happened yet.
/// </summary>
public interface IPickOutcomeRecorder
{
    Task RecordScanAsync(
        DateOnly scanDate,
        IEnumerable<StockCandidate> candidates,
        CancellationToken ct = default);
}

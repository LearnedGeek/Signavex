using Signavex.Domain.Models;

namespace Signavex.Domain.Interfaces;

/// <summary>
/// Fetches historical OHLCV for an explicit date range. Distinct from
/// <see cref="IMarketDataProvider"/>, which serves the live scan path
/// with a 15-minute in-memory cache. Historical implementations are
/// expected to use a persistent cache (data for past trading days never
/// changes) so repeat backtests don't burn rate-limit budget.
/// </summary>
public interface IHistoricalOhlcvProvider
{
    /// <summary>
    /// Returns daily OHLCV bars for the given ticker between <paramref name="from"/>
    /// and <paramref name="to"/>, inclusive. Adjusted-close prices (split- and
    /// dividend-adjusted) are required for multi-year backtests to be meaningful.
    /// </summary>
    Task<IReadOnlyList<OhlcvRecord>> GetHistoricalDailyOhlcvAsync(
        string ticker,
        DateOnly from,
        DateOnly to,
        CancellationToken ct = default);
}

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Signavex.Domain.Configuration;
using Signavex.Domain.Interfaces;
using Signavex.Domain.Models;
using Signavex.Infrastructure.Polygon.Dtos;

namespace Signavex.Infrastructure.Polygon;

/// <summary>
/// Fetches historical OHLCV from Polygon's <c>/v2/aggs</c> aggregates endpoint
/// for an explicit date range. One call returns up to 50,000 bars — a 5-year
/// daily series fits comfortably (~1,260 trading days). Adjusted-close prices
/// are required for multi-year backtests so corporate actions are reflected.
/// </summary>
public class PolygonHistoricalOhlcvProvider : IHistoricalOhlcvProvider
{
    private const int PolygonMaxLimit = 50_000;

    private readonly HttpClient _httpClient;
    private readonly DataProviderOptions _options;
    private readonly ILogger<PolygonHistoricalOhlcvProvider> _logger;

    public PolygonHistoricalOhlcvProvider(
        HttpClient httpClient,
        IOptions<DataProviderOptions> options,
        ILogger<PolygonHistoricalOhlcvProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<OhlcvRecord>> GetHistoricalDailyOhlcvAsync(
        string ticker,
        DateOnly from,
        DateOnly to,
        CancellationToken ct = default)
    {
        if (to < from)
            return Array.Empty<OhlcvRecord>();

        try
        {
            var url = $"/v2/aggs/ticker/{ticker}/range/1/day/{from:yyyy-MM-dd}/{to:yyyy-MM-dd}" +
                      $"?apiKey={_options.Polygon.ApiKey}&limit={PolygonMaxLimit}&sort=asc&adjusted=true";

            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var data = JsonSerializer.Deserialize<PolygonAggregatesResponse>(json);

            if (data?.Results is null or { Count: 0 })
                return Array.Empty<OhlcvRecord>();

            return data.Results
                .Select(r => new OhlcvRecord(
                    ticker,
                    DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(r.Timestamp).UtcDateTime),
                    r.Open,
                    r.High,
                    r.Low,
                    r.Close,
                    (long)r.Volume))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch historical OHLCV for {Ticker} {From}–{To}", ticker, from, to);
            return Array.Empty<OhlcvRecord>();
        }
    }
}

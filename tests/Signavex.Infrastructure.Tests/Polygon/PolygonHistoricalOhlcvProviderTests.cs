using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Signavex.Infrastructure.Polygon;
using Signavex.Infrastructure.Tests.Helpers;

namespace Signavex.Infrastructure.Tests.Polygon;

public class PolygonHistoricalOhlcvProviderTests
{
    private static PolygonHistoricalOhlcvProvider CreateProvider(MockHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.polygon.io") };
        return new PolygonHistoricalOhlcvProvider(httpClient, TestOptionsFactory.CreateDefault(), NullLogger<PolygonHistoricalOhlcvProvider>.Instance);
    }

    [Fact]
    public async Task GetHistoricalDailyOhlcvAsync_ParsesBars()
    {
        // Polygon's /v2/aggs response shape — `t` is millis since epoch.
        var json = """
        {
            "ticker": "AAPL",
            "results": [
                { "t": 1672790400000, "o": 130.28, "h": 130.90, "l": 124.17, "c": 125.07, "v": 112117500 },
                { "t": 1672876800000, "o": 126.89, "h": 128.66, "l": 125.08, "c": 126.36, "v": 89113600 }
            ]
        }
        """;
        var provider = CreateProvider(new MockHttpMessageHandler(json));

        var result = await provider.GetHistoricalDailyOhlcvAsync(
            "AAPL",
            new DateOnly(2023, 1, 1),
            new DateOnly(2023, 1, 5));

        Assert.Equal(2, result.Count);
        Assert.Equal("AAPL", result[0].Ticker);
        Assert.Equal(125.07m, result[0].Close);
        Assert.Equal(112_117_500L, result[0].Volume);
    }

    [Fact]
    public async Task GetHistoricalDailyOhlcvAsync_BuildsExpectedUrl()
    {
        var handler = new MockHttpMessageHandler("""{ "results": [] }""");
        var provider = CreateProvider(handler);

        await provider.GetHistoricalDailyOhlcvAsync(
            "MSFT",
            new DateOnly(2020, 1, 1),
            new DateOnly(2025, 1, 1));

        Assert.NotNull(handler.LastRequest);
        var url = handler.LastRequest.RequestUri!.ToString();
        Assert.Contains("/v2/aggs/ticker/MSFT/range/1/day/2020-01-01/2025-01-01", url);
        Assert.Contains("adjusted=true", url);
        Assert.Contains("sort=asc", url);
    }

    [Fact]
    public async Task GetHistoricalDailyOhlcvAsync_InvertedRange_ReturnsEmpty()
    {
        var provider = CreateProvider(new MockHttpMessageHandler("""{ "results": [] }"""));

        var result = await provider.GetHistoricalDailyOhlcvAsync(
            "AAPL",
            new DateOnly(2024, 1, 1),
            new DateOnly(2023, 1, 1));

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetHistoricalDailyOhlcvAsync_HttpError_ReturnsEmpty()
    {
        var provider = CreateProvider(new MockHttpMessageHandler("error", HttpStatusCode.InternalServerError));

        var result = await provider.GetHistoricalDailyOhlcvAsync(
            "AAPL",
            new DateOnly(2023, 1, 1),
            new DateOnly(2023, 12, 31));

        Assert.Empty(result);
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Signavex.Domain.Interfaces;
using Signavex.Domain.Models.Portfolio;
using Signavex.Engine.Portfolio;

namespace Signavex.Engine.Tests.Portfolio;

/// <summary>
/// Q2: stub engine + DI wiring. Verifies the interface resolves out of
/// the container and a no-op run round-trips an empty result. Real
/// simulation logic lands in Q4.
/// </summary>
public class PortfolioBacktesterTests
{
    private static PortfolioBacktestRequest SampleRequest() => new(
        StartDate: new DateOnly(2020, 1, 1),
        EndDate: new DateOnly(2025, 1, 1),
        StartingCapital: 100_000m,
        Universe: new[] { "AAPL", "MSFT" },
        Strategy: StrategyParameters.Default);

    [Fact]
    public async Task RunAsync_ReturnsEmptyResult_EchoingRequest()
    {
        var backtester = new PortfolioBacktester(NullLogger<PortfolioBacktester>.Instance);
        var request = SampleRequest();

        var result = await backtester.RunAsync(request);

        Assert.Same(request, result.Request);
        Assert.Empty(result.EquityCurve);
        Assert.Empty(result.Trades);
        Assert.Empty(result.OpenPositions);
        Assert.Equal(0, result.Metrics.TotalTrades);
        Assert.Equal(request.StartingCapital, result.Metrics.StartingEquity);
    }

    [Fact]
    public async Task DiContainer_ResolvesIPortfolioBacktester()
    {
        // Q2.3: confirms AddSignavexEngine wires IPortfolioBacktester → PortfolioBacktester.
        // Use a minimal scope — we only resolve IPortfolioBacktester, so missing infra
        // dependencies for sibling engine services don't matter.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignavexEngine();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var backtester = scope.ServiceProvider.GetRequiredService<IPortfolioBacktester>();

        Assert.IsType<PortfolioBacktester>(backtester);

        var result = await backtester.RunAsync(SampleRequest());
        Assert.Empty(result.Trades);
    }

    [Fact]
    public async Task RunAsync_RespectsCancellation()
    {
        // Stub doesn't actually do work, so cancellation just needs to not throw.
        // Still worth pinning so the contract holds when Q4 lands actual logic.
        var backtester = new PortfolioBacktester(NullLogger<PortfolioBacktester>.Instance);
        using var cts = new CancellationTokenSource();

        var result = await backtester.RunAsync(SampleRequest(), cts.Token);

        Assert.NotNull(result);
    }
}

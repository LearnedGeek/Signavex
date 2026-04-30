using Microsoft.Extensions.Logging;
using Signavex.Domain.Interfaces;
using Signavex.Domain.Models.Portfolio;

namespace Signavex.Engine.Portfolio;

/// <summary>
/// Q2 stub. Returns an <see cref="PortfolioBacktestResult.Empty"/> result so
/// callers can wire up DI and the request/response plumbing end-to-end before
/// the simulation logic lands in Q4.
/// </summary>
public class PortfolioBacktester : IPortfolioBacktester
{
    private readonly ILogger<PortfolioBacktester> _logger;

    public PortfolioBacktester(ILogger<PortfolioBacktester> logger)
    {
        _logger = logger;
    }

    public Task<PortfolioBacktestResult> RunAsync(PortfolioBacktestRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Quantback stub invoked for {StartDate}–{EndDate}, {TickerCount} tickers, ${StartingCapital}. Returning empty result (Q2 stub).",
            request.StartDate, request.EndDate, request.Universe.Count, request.StartingCapital);

        var now = DateTime.UtcNow;
        return Task.FromResult(PortfolioBacktestResult.Empty(request, now));
    }
}

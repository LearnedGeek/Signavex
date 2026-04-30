using Signavex.Domain.Interfaces;
using Signavex.Domain.Models.Portfolio;

namespace Signavex.Web.Services;

/// <summary>
/// Bridges <see cref="IPortfolioBacktester"/> to the Blazor UI. Singleton —
/// holds the latest completed result so any Pro user can view it without
/// triggering their own (potentially expensive) backtest. Admins kick off
/// new runs.
/// </summary>
public class QuantbackRunnerService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<QuantbackRunnerService> _logger;
    private readonly object _lock = new();

    private PortfolioBacktestResult? _latestResult;
    private PortfolioBacktestRequest? _runningRequest;
    private bool _isRunning;
    private string? _lastError;
    private DateTime? _runStartedAt;

    public QuantbackRunnerService(IServiceScopeFactory scopeFactory, ILogger<QuantbackRunnerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public PortfolioBacktestResult? LatestResult { get { lock (_lock) return _latestResult; } }
    public PortfolioBacktestRequest? RunningRequest { get { lock (_lock) return _runningRequest; } }
    public bool IsRunning { get { lock (_lock) return _isRunning; } }
    public string? LastError { get { lock (_lock) return _lastError; } }
    public DateTime? RunStartedAt { get { lock (_lock) return _runStartedAt; } }

    /// <summary>
    /// Kicks off a backtest in the background. Returns immediately. Caller
    /// should poll <see cref="IsRunning"/>/<see cref="LatestResult"/> for
    /// completion. Returns <c>false</c> if a run is already in flight.
    /// </summary>
    public bool TryStartRun(PortfolioBacktestRequest request)
    {
        lock (_lock)
        {
            if (_isRunning) return false;
            _isRunning = true;
            _runningRequest = request;
            _lastError = null;
            _runStartedAt = DateTime.UtcNow;
        }

        // Fire-and-forget. Errors land in _lastError; we deliberately don't
        // await this so the form POST returns quickly.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var backtester = scope.ServiceProvider.GetRequiredService<IPortfolioBacktester>();
                var result = await backtester.RunAsync(request);

                lock (_lock) { _latestResult = result; }
                _logger.LogInformation(
                    "Quantback completed: {TradeCount} trades, total return {Return:P2}",
                    result.Trades.Count, result.Metrics.TotalReturnPct);
            }
            catch (Exception ex)
            {
                lock (_lock) { _lastError = ex.Message; }
                _logger.LogError(ex, "Quantback failed");
            }
            finally
            {
                lock (_lock) { _isRunning = false; _runningRequest = null; }
            }
        });

        return true;
    }
}

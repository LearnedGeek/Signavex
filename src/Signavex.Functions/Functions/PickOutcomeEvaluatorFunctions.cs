using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Signavex.Functions.Orchestrators;
using Signavex.Functions.Security;

namespace Signavex.Functions.Functions;

public class PickOutcomeEvaluatorFunctions
{
    private readonly PickOutcomeEvaluatorOrchestrator _orchestrator;
    private readonly AdminKeyAuthorizer _authorizer;
    private readonly ILogger<PickOutcomeEvaluatorFunctions> _logger;

    public PickOutcomeEvaluatorFunctions(
        PickOutcomeEvaluatorOrchestrator orchestrator,
        AdminKeyAuthorizer authorizer,
        ILogger<PickOutcomeEvaluatorFunctions> logger)
    {
        _orchestrator = orchestrator;
        _authorizer = authorizer;
        _logger = logger;
    }

    // 12:30 AM UTC daily — runs after the daily scan (10pm) and brief
    // (11:30pm), so today's picks are captured but every horizon's target
    // date is stable. NCRONTAB: {sec} {min} {hour} {day} {month} {dow}
    [Function("PickOutcomeEvaluatorDaily")]
    public async Task EvaluateDaily(
        [TimerTrigger("0 30 0 * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        _logger.LogInformation("FT2 evaluator timer fired at {Time} UTC", DateTime.UtcNow);
        var result = await _orchestrator.RunCycleAsync(ct);
        _logger.LogInformation(
            "FT2 evaluator complete: {Touched} rows touched, {Entries} entries resolved, {Horizons} horizons filled, {Errors} errors",
            result.RowsTouched, result.EntriesResolved, result.HorizonsFilled, result.Errors);
    }

    // Manual admin trigger for backfill catchup or smoke-testing.
    [Function("PickOutcomeEvaluatorHttp")]
    public async Task<IActionResult> EvaluateHttp(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ops/evaluate-pick-outcomes")] HttpRequest req,
        CancellationToken ct)
    {
        if (!_authorizer.Authorize(req))
            return new UnauthorizedResult();

        _logger.LogInformation("Admin-triggered FT2 evaluation via HTTP");
        var result = await _orchestrator.RunCycleAsync(ct);
        return new OkObjectResult(new
        {
            rowsTouched = result.RowsTouched,
            entriesResolved = result.EntriesResolved,
            horizonsFilled = result.HorizonsFilled,
            errors = result.Errors,
        });
    }
}

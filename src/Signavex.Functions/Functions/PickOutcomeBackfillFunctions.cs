using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Signavex.Functions.Orchestrators;
using Signavex.Functions.Security;

namespace Signavex.Functions.Functions;

public class PickOutcomeBackfillFunctions
{
    private readonly PickOutcomeBackfillOrchestrator _orchestrator;
    private readonly AdminKeyAuthorizer _authorizer;
    private readonly ILogger<PickOutcomeBackfillFunctions> _logger;

    public PickOutcomeBackfillFunctions(
        PickOutcomeBackfillOrchestrator orchestrator,
        AdminKeyAuthorizer authorizer,
        ILogger<PickOutcomeBackfillFunctions> logger)
    {
        _orchestrator = orchestrator;
        _authorizer = authorizer;
        _logger = logger;
    }

    // One-shot retroactive populate of PickOutcomes from existing scan
    // runs. Admin-only via the shared admin-key header. Returns counts so
    // the caller can confirm the backfill did something.
    [Function("PickOutcomeBackfillHttp")]
    public async Task<IActionResult> BackfillHttp(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ops/backfill-pick-outcomes")] HttpRequest req,
        CancellationToken ct)
    {
        if (!_authorizer.Authorize(req))
            return new UnauthorizedResult();

        _logger.LogInformation("Admin-triggered FT3 backfill via HTTP");
        var result = await _orchestrator.RunAsync(ct);
        return new OkObjectResult(new
        {
            scansProcessed = result.ScansProcessed,
            newRowsPersisted = result.NewRowsPersisted,
            totalScansFound = result.TotalScansFound,
        });
    }
}

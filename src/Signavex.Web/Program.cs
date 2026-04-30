using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Signavex.Domain.Configuration;
using Signavex.Engine;
using Signavex.Infrastructure;
using Signavex.Infrastructure.Email;
using Signavex.Infrastructure.Persistence;
using Signavex.Signals;
using Stripe;
using Stripe.Checkout;
using Signavex.Web.Components;
using Signavex.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Bind configuration options
builder.Services.Configure<SignavexOptions>(
    builder.Configuration.GetSection(SignavexOptions.SectionName));

builder.Services.Configure<DataProviderOptions>(
    builder.Configuration.GetSection(DataProviderOptions.SectionName));

builder.Services.Configure<AnthropicOptions>(
    builder.Configuration.GetSection(AnthropicOptions.SectionName));

builder.Services.Configure<StripeOptions>(
    builder.Configuration.GetSection(StripeOptions.SectionName));

builder.Services.Configure<EmailOptions>(
    builder.Configuration.GetSection(EmailOptions.SectionName));

var providerOptions = builder.Configuration
    .GetSection(DataProviderOptions.SectionName)
    .Get<DataProviderOptions>() ?? new DataProviderOptions();

var signavexOptions = builder.Configuration
    .GetSection(SignavexOptions.SectionName)
    .Get<SignavexOptions>() ?? new SignavexOptions();

// Register domain layers
builder.Services
    .AddSignavexSignals()
    .AddSignavexEngine()
    .AddSignavexInfrastructure(providerOptions, signavexOptions.ConnectionString);

// ASP.NET Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequiredLength = 8;
    options.Password.RequireNonAlphanumeric = false;
    options.SignIn.RequireConfirmedAccount = false;
})
.AddEntityFrameworkStores<SignavexDbContext>()
.AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, SendGridEmailSender>();

// Google OAuth — Sign in with Google for reduced login friction.
// Requires a Google Cloud project with OAuth consent screen configured.
// Client ID and secret are read from configuration (Google:ClientId, Google:ClientSecret).
// If not configured, the Google button on the login page is hidden gracefully.
var googleClientId = builder.Configuration["Google:ClientId"];
var googleClientSecret = builder.Configuration["Google:ClientSecret"];
if (!string.IsNullOrEmpty(googleClientId) && !string.IsNullOrEmpty(googleClientSecret))
{
    builder.Services.AddAuthentication()
        .AddGoogle(options =>
        {
            options.ClientId = googleClientId;
            options.ClientSecret = googleClientSecret;
            // Explicit: sign the external login result into the scheme that
            // SignInManager.GetExternalLoginInfoAsync() reads from. Relying on
            // the AddIdentity-provided DefaultSignInScheme is flaky across
            // .NET versions — being explicit avoids mysterious null returns.
            options.SignInScheme = IdentityConstants.ExternalScheme;
        });
}

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/account/login";
    options.LogoutPath = "/account/logout";
    options.AccessDeniedPath = "/account/login";
    options.Cookie.HttpOnly = true;
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
});

// Application services
builder.Services.AddSingleton<ScanDashboardService>();
builder.Services.AddSingleton<BacktestRunnerService>();
builder.Services.AddSingleton<QuantbackRunnerService>();
builder.Services.AddSingleton<ApiKeyValidationService>();
builder.Services.AddSingleton<EconomicDashboardService>();
builder.Services.AddSingleton<DailyBriefService>();

// HttpClient for calling Azure Functions admin endpoints. Named client
// "functions" carries the base URL + admin key header on every request.
// Base URL + key are supplied by Terraform via Functions__Url + Functions__AdminKey.
builder.Services.AddHttpClient("functions", (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var baseUrl = config["Functions:Url"];
    if (!string.IsNullOrEmpty(baseUrl))
    {
        client.BaseAddress = new Uri(baseUrl);
    }
    var adminKey = config["Functions:AdminKey"];
    if (!string.IsNullOrEmpty(adminKey))
    {
        client.DefaultRequestHeaders.Add("x-signavex-admin-key", adminKey);
    }
    client.Timeout = TimeSpan.FromSeconds(60);
});

// Authorization policies
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("ProRequired", policy => policy.RequireRole("Pro", "Admin"));

// Health checks
builder.Services.AddHealthChecks()
    .AddDbContextCheck<SignavexDbContext>();

// Rate limiting for public pages
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("public", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

// Blazor — Static SSR only, no interactive server mode (no SignalR circuit)
builder.Services.AddRazorComponents();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider,
    Microsoft.AspNetCore.Components.Server.ServerAuthenticationStateProvider>();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

// Configure Stripe API key
var stripeOptions = builder.Configuration
    .GetSection(StripeOptions.SectionName)
    .Get<StripeOptions>() ?? new StripeOptions();
StripeConfiguration.ApiKey = stripeOptions.SecretKey;

// Initialize database — migrations handle all schema and seed data
using (var scope = app.Services.CreateScope())
{
    await using var db = await scope.ServiceProvider
        .GetRequiredService<IDbContextFactory<SignavexDbContext>>()
        .CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

// Friendly status-code pages — re-executes the request internally against
// /Error/{code} while preserving the original status code in the response.
// Catches 404, 403, etc. routed through MVC/Razor; static file 404s pass through.
app.UseStatusCodePagesWithReExecute("/Error/{0}");

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapHealthChecks("/health");

// Home route ("/") is handled by Landing.razor — a Razor component that
// shows the marketing landing page for anonymous users and redirects
// authenticated users to /today (with onboarding/trial checks).

// Google OAuth: initiate the external login challenge. Uses SignInManager's
// ConfigureExternalAuthenticationProperties helper so the properties include
// the LoginProvider key that GetExternalLoginInfoAsync needs when the callback
// runs — without this, the callback receives the cookie but can't reconstruct
// the ExternalLoginInfo and returns null.
app.MapGet("/account/login-google", (SignInManager<ApplicationUser> signInManager) =>
{
    var properties = signInManager.ConfigureExternalAuthenticationProperties("Google", "/account/google-callback");
    return Results.Challenge(properties, ["Google"]);
});

// Google OAuth: callback after Google authenticates the user.
// Creates a local account if this is the user's first Google sign-in.
app.MapGet("/account/google-callback", async (
    HttpContext ctx,
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager) =>
{
    var info = await signInManager.GetExternalLoginInfoAsync();
    if (info is null)
        return Results.Redirect("/account/login?error=google-failed");

    // Try to sign in with the existing external login
    var result = await signInManager.ExternalLoginSignInAsync(
        info.LoginProvider, info.ProviderKey, isPersistent: true);

    if (result.Succeeded)
        return Results.Redirect("/");

    // First-time Google login — create a local account
    var email = info.Principal.FindFirstValue(System.Security.Claims.ClaimTypes.Email);
    if (string.IsNullOrEmpty(email))
        return Results.Redirect("/account/login?error=google-no-email");

    // Check if an account with this email already exists (registered via password)
    var existingUser = await userManager.FindByEmailAsync(email);
    if (existingUser is not null)
    {
        // Link Google to the existing account
        await userManager.AddLoginAsync(existingUser, info);
        await signInManager.SignInAsync(existingUser, isPersistent: true);
        return Results.Redirect("/");
    }

    // Brand-new user — create account + start trial
    var user = new ApplicationUser
    {
        UserName = email,
        Email = email,
        EmailConfirmed = true,
        TrialStartedAt = DateTime.UtcNow
    };

    var createResult = await userManager.CreateAsync(user);
    if (!createResult.Succeeded)
        return Results.Redirect("/account/login?error=google-create-failed");

    await userManager.AddToRoleAsync(user, "Free");
    await userManager.AddLoginAsync(user, info);
    await signInManager.SignInAsync(user, isPersistent: true);
    return Results.Redirect("/");
});

// Welcome onboarding completion — marks the user as onboarded and sets the
// trial start time if not already set (handles accounts created before this
// feature existed).
app.MapPost("/welcome/complete", async (
    HttpContext ctx,
    UserManager<ApplicationUser> userManager) =>
{
    var user = await userManager.GetUserAsync(ctx.User);
    if (user is not null)
    {
        user.HasCompletedOnboarding = true;
        if (!user.TrialStartedAt.HasValue)
            user.TrialStartedAt = DateTime.UtcNow;
        await userManager.UpdateAsync(user);
    }
    return Results.Redirect("/today");
}).RequireAuthorization();

app.MapPost("/account/logout", async (SignInManager<ApplicationUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.Redirect("/account/login");
}).DisableAntiforgery();

// Stripe: Create checkout session for Pro upgrade
app.MapPost("/account/upgrade", async (
    HttpContext httpContext,
    UserManager<ApplicationUser> userManager,
    Microsoft.Extensions.Options.IOptions<StripeOptions> stripeOpts) =>
{
    var user = await userManager.GetUserAsync(httpContext.User);
    if (user is null) return Results.Redirect("/account/login");

    // Create or reuse Stripe customer
    if (string.IsNullOrEmpty(user.StripeCustomerId))
    {
        var customerService = new CustomerService();
        var customer = await customerService.CreateAsync(new CustomerCreateOptions
        {
            Email = user.Email,
            Metadata = new Dictionary<string, string> { { "UserId", user.Id } }
        });
        user.StripeCustomerId = customer.Id;
        await userManager.UpdateAsync(user);
    }

    var opts = stripeOpts.Value;
    var sessionService = new SessionService();
    var session = await sessionService.CreateAsync(new SessionCreateOptions
    {
        Customer = user.StripeCustomerId,
        PaymentMethodTypes = ["card"],
        LineItems = [new SessionLineItemOptions { Price = opts.ProPriceId, Quantity = 1 }],
        Mode = "subscription",
        SuccessUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/account/upgrade-success",
        CancelUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/account/upgrade-cancelled"
    });

    return Results.Redirect(session.Url);
}).RequireAuthorization();

// Stripe: Webhook endpoint
app.MapPost("/webhooks/stripe", async (
    HttpContext httpContext,
    UserManager<ApplicationUser> userManager,
    Microsoft.Extensions.Options.IOptions<StripeOptions> stripeOpts,
    ILogger<Program> logger) =>
{
    var json = await new StreamReader(httpContext.Request.Body).ReadToEndAsync();
    try
    {
        var stripeEvent = EventUtility.ConstructEvent(
            json,
            httpContext.Request.Headers["Stripe-Signature"],
            stripeOpts.Value.WebhookSecret);

        switch (stripeEvent.Type)
        {
            case EventTypes.CheckoutSessionCompleted:
            {
                var session = stripeEvent.Data.Object as Session;
                if (session?.CustomerId is not null)
                {
                    var user = await FindUserByStripeCustomerId(userManager, session.CustomerId);
                    if (user is not null)
                    {
                        user.SubscriptionPlan = "Pro";
                        await userManager.UpdateAsync(user);
                        if (!await userManager.IsInRoleAsync(user, "Pro"))
                            await userManager.AddToRoleAsync(user, "Pro");
                        if (await userManager.IsInRoleAsync(user, "Free"))
                            await userManager.RemoveFromRoleAsync(user, "Free");
                        logger.LogInformation("User {UserId} upgraded to Pro", user.Id);
                    }
                }
                break;
            }
            case EventTypes.CustomerSubscriptionDeleted:
            {
                var subscription = stripeEvent.Data.Object as Subscription;
                if (subscription?.CustomerId is not null)
                {
                    var user = await FindUserByStripeCustomerId(userManager, subscription.CustomerId);
                    if (user is not null)
                    {
                        user.SubscriptionPlan = "Free";
                        await userManager.UpdateAsync(user);
                        if (await userManager.IsInRoleAsync(user, "Pro"))
                            await userManager.RemoveFromRoleAsync(user, "Pro");
                        if (!await userManager.IsInRoleAsync(user, "Free"))
                            await userManager.AddToRoleAsync(user, "Free");
                        logger.LogInformation("User {UserId} downgraded to Free", user.Id);
                    }
                }
                break;
            }
            case EventTypes.InvoicePaymentFailed:
            {
                var invoice = stripeEvent.Data.Object as Invoice;
                logger.LogWarning("Payment failed for Stripe customer {CustomerId}", invoice?.CustomerId);
                break;
            }
        }

        return Results.Ok();
    }
    catch (StripeException ex)
    {
        logger.LogWarning(ex, "Stripe webhook signature verification failed");
        return Results.BadRequest();
    }
}).DisableAntiforgery();

// Stripe: Customer portal for billing management
app.MapPost("/account/billing-portal", async (
    HttpContext httpContext,
    UserManager<ApplicationUser> userManager) =>
{
    var user = await userManager.GetUserAsync(httpContext.User);
    if (user?.StripeCustomerId is null) return Results.Redirect("/");

    var portalService = new Stripe.BillingPortal.SessionService();
    var session = await portalService.CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
    {
        Customer = user.StripeCustomerId,
        ReturnUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/account/manage"
    });

    return Results.Redirect(session.Url);
}).RequireAuthorization();

// =============================================================================
// Admin endpoints — Admin role only
// All expensive operations live here. Non-admin users have no way to trigger them.
// =============================================================================

// Admin endpoints that delegate to Azure Functions (Worker replacement).
// Each handler POSTs to the corresponding Function HTTP trigger. The named
// "functions" HttpClient already carries the base URL + admin key header.
// Fire-and-forget: we return the redirect immediately and let the Function
// run in the background — the admin page shows a "started" banner.
app.MapPost("/admin/scan", async (
    IHttpClientFactory httpFactory,
    ILogger<Program> logger) =>
{
    var client = httpFactory.CreateClient("functions");
    _ = Task.Run(async () =>
    {
        try { await client.PostAsync("api/ops/scan", content: null); }
        catch (Exception ex) { logger.LogError(ex, "Failed to invoke Functions scan"); }
    });
    return Results.Redirect("/admin?action=scan");
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPost("/admin/sync-economic", async (
    IHttpClientFactory httpFactory,
    ILogger<Program> logger) =>
{
    var client = httpFactory.CreateClient("functions");
    _ = Task.Run(async () =>
    {
        try { await client.PostAsync("api/ops/sync-economic", content: null); }
        catch (Exception ex) { logger.LogError(ex, "Failed to invoke Functions sync-economic"); }
    });
    return Results.Redirect("/admin?action=sync-economic");
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPost("/admin/generate-brief", async (
    IHttpClientFactory httpFactory,
    ILogger<Program> logger) =>
{
    var client = httpFactory.CreateClient("functions");
    _ = Task.Run(async () =>
    {
        try { await client.PostAsync("api/ops/generate-brief", content: null); }
        catch (Exception ex) { logger.LogError(ex, "Failed to invoke Functions generate-brief"); }
    });
    return Results.Redirect("/admin?action=generate-brief");
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPost("/admin/run-backtest", (BacktestRunnerService backtest, HttpContext ctx) =>
{
    var asOfStr = ctx.Request.Form["asOfDate"].ToString();
    if (!DateOnly.TryParse(asOfStr, out var asOf))
        asOf = DateOnly.FromDateTime(DateTime.Today.AddMonths(-1));

    // Fire and forget — backtest takes minutes, user must refresh to see result
    _ = Task.Run(() => backtest.RunBacktestAsync(asOf));

    return Results.Redirect("/admin?action=run-backtest");
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

// Quantback (portfolio-simulation backtest) — admin triggers a run with a
// configured request; the singleton holds the latest result for any Pro
// user to view at /quantback. Cold cache may take many minutes for larger
// universes (~5 req/min Polygon free tier).
app.MapPost("/admin/run-quantback", (QuantbackRunnerService runner, HttpContext ctx) =>
{
    var form = ctx.Request.Form;

    var startStr = form["startDate"].ToString();
    var endStr = form["endDate"].ToString();
    var universeStr = form["universe"].ToString();
    var capitalStr = form["startingCapital"].ToString();

    var start = DateOnly.TryParse(startStr, out var s) ? s : DateOnly.FromDateTime(DateTime.Today.AddYears(-1));
    var end = DateOnly.TryParse(endStr, out var e) ? e : DateOnly.FromDateTime(DateTime.Today);
    var capital = decimal.TryParse(capitalStr, out var c) ? c : 100_000m;

    // Universe presets — keep this short; users on free tier will hit rate
    // limits on large universes anyway.
    string[] universe = universeStr switch
    {
        "top10" => new[] { "AAPL", "MSFT", "AMZN", "GOOGL", "META", "NVDA", "TSLA", "JPM", "V", "WMT" },
        "fang"  => new[] { "META", "AMZN", "AAPL", "NFLX", "GOOGL" },
        _       => new[] { "AAPL", "MSFT", "AMZN", "GOOGL", "META", "NVDA", "TSLA", "JPM", "V", "WMT" },
    };

    var strategy = Signavex.Domain.Models.Portfolio.StrategyParameters.Default;
    var request = new Signavex.Domain.Models.Portfolio.PortfolioBacktestRequest(
        StartDate: start,
        EndDate: end,
        StartingCapital: capital,
        Universe: universe,
        Strategy: strategy);

    runner.TryStartRun(request);
    return Results.Redirect("/quantback");
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

// =============================================================================
// CSV export endpoints — available to authenticated users (any role)
// These are read-only data downloads, not expensive operations.
// =============================================================================

app.MapGet("/picks/export.csv", async (ScanDashboardService dashboard) =>
{
    var result = await dashboard.GetLatestResultAsync();
    if (result is null || result.Candidates.Count == 0)
        return Results.NotFound();

    var csv = Signavex.Domain.Helpers.CsvExportHelper.GenerateCsv(result.Candidates);
    var fileName = $"signavex-scan-{DateTime.UtcNow:yyyy-MM-dd}.csv";
    return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", fileName);
}).RequireAuthorization();

app.MapGet("/backtest/export.csv", (BacktestRunnerService backtest) =>
{
    var result = backtest.LatestResult;
    if (result is null || result.Candidates.Count == 0)
        return Results.NotFound();

    var csv = Signavex.Domain.Helpers.CsvExportHelper.GenerateCsv(result.Candidates);
    var fileName = $"signavex-backtest-{result.AsOfDate:yyyy-MM-dd}.csv";
    return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", fileName);
}).RequireAuthorization();

app.MapRazorComponents<App>();

app.Run();

static async Task<ApplicationUser?> FindUserByStripeCustomerId(
    UserManager<ApplicationUser> userManager, string stripeCustomerId)
{
    return await userManager.Users
        .FirstOrDefaultAsync(u => u.StripeCustomerId == stripeCustomerId);
}

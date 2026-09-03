using Lanyard.App.Components;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Announcements;
using Lanyard.Application.Services.ApplicationRoles;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Gdpr;
using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Training;
using Lanyard.Application.SignalR;
using Lanyard.Application.SignalR.Events;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Application.Services.Time;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Kitchen;
using Lanyard.Application.Services.Legal;
using Lanyard.API;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Security.Claims;
using Lanyard.App.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Lanyard.Application.Services.Clients;
using Lanyard.Application.Services.VideoStreaming;
using Lanyard.App.Components.Layout;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Load local, git-ignored overrides (e.g. Clients:SharedSecret, connection strings) when present.
// Added after the default sources so it takes precedence for local development; it is optional and
// absent in production, where environment variables supply these values instead.
builder.Configuration
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.local.json", optional: true, reloadOnChange: true);

if (builder.Environment.IsDevelopment() == false)
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? "8080"}");
}

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "Lanyard.Server",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString(),
        serviceInstanceId: Environment.MachineName))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation())
    .WithLogging()
    .UseOtlpExporter();

// Add Razor Components with Interactive Server
builder.Services.AddRazorComponents(options => options.DetailedErrors = builder.Environment.IsDevelopment())
    .AddInteractiveServerComponents();

// Add HttpContextAccessor for accessing the current user
builder.Services.AddHttpContextAccessor();

builder.Services.AddScoped<ISecurityService, SecurityService>();
builder.Services.AddScoped<IGdprService, GdprService>();
builder.Services.AddSingleton<IClientSecretValidator, ClientSecretValidator>();
// Separate from the kiosk secret above on purpose: Reach is internet-facing and serves anonymous
// customers, so sharing one secret would let a compromise of the public site drive the light rig.
builder.Services.AddSingleton<IReachApiCredentialValidator, ReachApiCredentialValidator>();
builder.Services.AddScoped<ITenantDirectoryService, TenantDirectoryService>();
builder.Services.AddScoped<IOrderingAvailabilityService, OrderingAvailabilityService>();
builder.Services.AddScoped<IReceiptPrintService, ReceiptPrintService>();
builder.Services.AddScoped<IMenuService, MenuService>();
builder.Services.AddScoped<IQrTableTokenService, QrTableTokenService>();
builder.Services.AddScoped<IKitchenOrderService, KitchenOrderService>();
builder.Services.AddSingleton<IOrderPaymentService, StripeOrderPaymentService>();
builder.Services.AddHostedService<AbandonedOrderSweepHostedService>();
builder.Services.AddHostedService<OrderNoteRetentionHostedService>();
builder.Services.AddSingleton<KitchenOrderEvents>();
builder.Services.AddSingleton<IKitchenHubNotifier, KitchenHubNotifier>();
builder.Services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();
builder.Services.AddScoped<IFileService, FileService>();
builder.Services.AddScoped<ApplicationRolesService>();
builder.Services.AddScoped<IPlaylistService, PlaylistService>();
builder.Services.AddScoped<IMusicService, MusicService>();
builder.Services.AddScoped<IClientService, ClientService>();
builder.Services.AddScoped<IProjectionProgramService, ProjectionProgramService>();
builder.Services.AddScoped<ICourseService, CourseService>();
builder.Services.AddScoped<ICourseAssignmentService, CourseAssignmentService>();
builder.Services.AddScoped<ITrainingAnalyticsService, TrainingAnalyticsService>();
builder.Services.AddScoped<ITrainingBrandingResolver, TrainingBrandingResolver>();
builder.Services.AddScoped<ICertificateService, CertificateService>();
builder.Services.AddScoped<ICompanyAccessService, CompanyAccessService>();
builder.Services.AddScoped<ICompanyPayoutAccountService, CompanyPayoutAccountService>();
builder.Services.AddScoped<ICompanyLocationService, CompanyLocationService>();
builder.Services.AddScoped<ICompanyLegalDocumentService, CompanyLegalDocumentService>();
builder.Services.AddScoped<ICurrentLocationContext, CurrentLocationContextService>();
builder.Services.AddHostedService<CourseRecurrenceHostedService>();
builder.Services.AddHostedService<TrainingDueSoonHostedService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<ISignalRProjectionControlHub, SignalRControlHub>();
builder.Services.AddScoped<ITimeService, TimeService>();
builder.Services.AddScoped<IDmxSceneService, DmxSceneService>();

builder.Services.AddSingleton<ILaserGameStatusStore, LaserGameStatusStore>();
builder.Services.AddScoped<IGameResultService, GameResultService>();
builder.Services.AddSingleton<SignalRProjectionControlHubEvents>();
builder.Services.AddSingleton<IVideoStreamTokenService, VideoStreamTokenService>();
builder.Services.AddSingleton<IVideoStreamSignalingService, VideoStreamSignalingService>();
builder.Services.Configure<VideoStreamingOptions>(builder.Configuration.GetSection("VideoStreaming"));
builder.Services.AddSingleton<MusicPlayerService>();
builder.Services.AddScoped<ISongAnalysisService, SongAnalysisService>();
builder.Services.AddSingleton<ISongAnalysisQueue, SongAnalysisQueue>();
builder.Services.AddSingleton<IBeatClockService, BeatClockService>();
builder.Services.AddHostedService<SongAnalysisHostedService>();
builder.Services.AddSingleton<DmxService>();
builder.Services.AddSingleton<IDmxService>(sp => sp.GetRequiredService<DmxService>());
builder.Services.AddSingleton<IDmxClientService>(sp => sp.GetRequiredService<DmxService>());
builder.Services.AddSingleton<IDmxSceneRunnerService, DmxSceneRunnerService>();

builder.Services.AddSingleton<IProjectionProgramRunnerService, ProjectionProgramRunnerService>();
builder.Services.AddHostedService<ProjectionProgramCompletionListener>();

builder.Services.AddSingleton<AutomationEngineService>();
builder.Services.AddSingleton<IActionExecutor, MusicControlActionExecutor>();
builder.Services.AddSingleton<IActionExecutor, StartProjectionProgramActionExecutor>();
builder.Services.AddSingleton<IActionExecutor, StopProjectionProgramActionExecutor>();
builder.Services.AddSingleton<IActionExecutor, DmxSceneControlActionExecutor>();
builder.Services.AddSingleton<IActionExecutor, ProjectionProgramControlActionExecutor>();
builder.Services.AddScoped<IAutomationRuleService, AutomationRuleService>();
builder.Services.AddScoped<IAutomationLogService, AutomationLogService>();
builder.Services.AddHostedService<AutomationEngineHostedService>();
builder.Services.AddHostedService<IdleTriggerHostedService>();
builder.Services.AddHostedService<ScheduledTriggerHostedService>();

builder.Services.AddScoped<IClientZoneScoreboardService, ClientZoneScoreboardService>();

builder.Services.AddScoped<IAnnouncementService, AnnouncementService>();

builder.Services.AddSignalR();

builder.Services.AddScoped<DragStateService>();

string? informationalVersion = Assembly
    .GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion ?? "0.0.0";

builder.Services.AddSingleton(new AppInfo
{
    Version = informationalVersion
});

builder.Services.AddSingleton<IReleaseNotesService, ReleaseNotesService>();

// Configure Database
string? connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'DefaultConnection' is not configured. Set the "
        + "ConnectionStrings__DefaultConnection environment variable (production) or run the "
        + "local docker-compose Postgres for development.");
}

// Outside Development, kiosk clients authenticate to the file/audio endpoints and the
// /websocket control hub with this shared secret and nothing else - there is no user login for
// them to fall back to. Failing here means a misconfigured deploy never reaches a serving state,
// instead of quietly running with every kiosk endpoint open to anyone who can reach the host.
if (builder.Environment.IsDevelopment() == false && string.IsNullOrWhiteSpace(builder.Configuration["Clients:SharedSecret"]))
{
    throw new InvalidOperationException(
        "Clients:SharedSecret is not configured. Set the Clients__SharedSecret environment variable "
        + "before starting the server outside Development - the client file/audio endpoints and the "
        + "/websocket control hub must not run with kiosk authentication disabled.");
}

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString, b => b.MigrationsAssembly("Lanyard.Infrastructure")));

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddDatabaseDeveloperPageExceptionFilter();
}

// Configure Identity with minimal settings
builder.Services.AddIdentity<UserProfile, ApplicationRole>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
    options.Password.RequireNonAlphanumeric = false;

    // Brute-force protection: lock an account after repeated failed sign-ins.
    options.Lockout.AllowedForNewUsers = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddSignInManager()
.AddDefaultTokenProviders();

builder.Services.Configure<DataProtectionTokenProviderOptions>(options =>
{
    // Default token lifespan is 1 day; extended for the invite-email use case, where the
    // email may sit unread longer than a same-session password reset. ChangePasswordAsync
    // (admin-driven) generates and consumes its token in the same call, so this is safe there too.
    options.TokenLifespan = TimeSpan.FromDays(7);
});

// ASP.NET Identity's SecurityStampValidator periodically (every 30 minutes by default)
// rebuilds the cookie principal from the user/role store via CreateUserPrincipalAsync.
// The location claim is issued at sign-in only and is not backed by the user store, so
// without this hook it would be silently dropped from the refreshed principal - breaking
// every location-scoped page for any session that outlives the validation interval.
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
{
    options.OnRefreshingPrincipal = context =>
    {
        Claim? locationClaim = context.CurrentPrincipal?.FindFirst(LocationClaimTypes.LocationId);

        if (locationClaim is not null && context.NewPrincipal?.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(locationClaim);
        }

        return Task.CompletedTask;
    };
});

builder.Services.AddAuthorization();

// Configure cookie to persist login across sessions
builder.Services.ConfigureApplicationCookie(options =>
{
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
    options.LoginPath = "/HandleLogin";
    options.LogoutPath = "/HandleLogout";
    options.AccessDeniedPath = "/HandleLogin";

    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;

    // Only send the auth cookie over HTTPS in non-development environments (dev may run on
    // plain-HTTP localhost). Prevents the session cookie leaking over cleartext in production.
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});

if (builder.Environment.IsDevelopment() == false)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

builder.Services.AddMemoryCache();

// Add custom authentication state provider
builder.Services.AddScoped<AuthenticationStateProvider, IdentityAuthenticationStateProvider>();
builder.Services.AddCascadingAuthenticationState();

// Add Controllers for API endpoints
builder.Services.AddControllers();

// Add HttpClient
builder.Services.AddHttpClient();
builder.Services.AddScoped(sp =>
{
    NavigationManager navigationManager = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(navigationManager.BaseUri) };
});

builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddHttpClient<IEmailService, EmailService>(client =>
{
    client.BaseAddress = new Uri("https://api.resend.com/");
});

// Add FluentUI Components
builder.Services.AddFluentUIComponents(configuration =>
{
    configuration.Toast.Position = ToastPosition.TopEnd;
    configuration.Toast.Lifetime = TimeSpan.FromSeconds(5);
});

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("ip-fixed", httpContext =>
    {
        string ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 25,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
    });

    // The ordering API cannot use "ip-fixed": Reach proxies every customer's request
    // server-side, so the whole customer base shares Reach's single IP and a per-IP window
    // would cap an entire venue at 25 requests a minute between them. These partition on a
    // per-customer id Reach forwards instead - see Lanyard.API.OrderingRateLimits.
    options.AddPolicy(OrderingRateLimits.ReadPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            OrderingRateLimits.ResolvePartitionKey(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = OrderingRateLimits.ReadPermitLimit,
                Window = OrderingRateLimits.Window,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.AddPolicy(OrderingRateLimits.WritePolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            OrderingRateLimits.ResolvePartitionKey(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = OrderingRateLimits.WritePermitLimit,
                Window = OrderingRateLimits.Window,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    // Partitioned by IP rather than customer: Stripe does not send the per-customer header,
    // and a throttled webhook retry means a paid order never reaching the kitchen.
    options.AddPolicy(OrderingRateLimits.WebhookPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = OrderingRateLimits.WebhookPermitLimit,
                Window = OrderingRateLimits.Window,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsync("Too many requests.", token);
    };
});

builder.Services.AddDataProtection()
    .PersistKeysToDbContext<ApplicationDbContext>();

var app = builder.Build();

if (app.Environment.IsDevelopment() == false)
{
    app.UseForwardedHeaders();
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseRateLimiter();

string connectSrc = app.Environment.IsDevelopment() ? "'self' wss: ws://localhost:*" : "'self' wss:";

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

    context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";

    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' https://cdn.jsdelivr.net 'unsafe-inline'; " +
        "style-src 'self' https://cdn.jsdelivr.net 'unsafe-inline'; " +
        "font-src 'self' data:; " +
        "img-src 'self' data:; " +
        $"connect-src {connectSrc}; " +
        "frame-ancestors 'self';";

    await next();
});

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// Both IClientSecretValidator and ILoggerFactory are app-wide singletons, so resolving them once
// here - rather than via context.RequestServices inside the request delegate below - avoids a
// per-request DI resolve on every SignalR connection attempt without changing behavior.
IClientSecretValidator websocketGateValidator = app.Services.GetRequiredService<IClientSecretValidator>();
ILogger websocketGateLogger = app.Services
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("Lanyard.Application.Services.Authentication.ClientRequestAuthorization");

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/websocket"))
    {
        string? providedSecret = context.Request.Query["secret"].ToString();
        string remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";

        // Delegates to the same decision point the client REST endpoints use
        // (ClientRequestAuthorization.EvaluateAndLog / IClientSecretValidator.Authorize) so the
        // unconfigured-secret case can never be decided differently here than there.
        if (!ClientRequestAuthorization.EvaluateAndLog(websocketGateValidator, providedSecret, websocketGateLogger, remoteIp, context.Request.Path.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Invalid or missing client shared secret.");
            return;
        }
    }

    await next();
});

// Map SignalR hub for music control
app.MapHub<SignalRControlHub>("/websocket");

// Separate path from /websocket on purpose: that route is gated above by the kiosk shared
// secret, which is the wrong credential for a kitchen display. This hub authorises by staff
// role instead (see KitchenHub's [Authorize]).
app.MapHub<KitchenHub>("/kitchenhub");

// Deliberately no .RequireRateLimiting("ip-fixed") here. Applying a policy as a route
// convention makes it win over any [EnableRateLimiting] attribute on a controller - per the
// ASP.NET Core rate-limiting docs, the attribute is simply "not applied" when the endpoint
// already got a policy this way. That silently defeated the ordering API's own limits, so
// every controller now opts in explicitly instead: ip-fixed on the five that want per-IP
// limiting, and the ordering policies on OrderingController.
app.MapControllers();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

if (builder.Environment.IsDevelopment() == false)
{
    using (IServiceScope scope = app.Services.CreateScope())
    {
        IDbContextFactory<ApplicationDbContext> factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        await using ApplicationDbContext db = await factory.CreateDbContextAsync();

        await db.Database.MigrateAsync();
    }
}

await DatabaseSeeder.SeedAsync(app.Services);

app.Run();

public partial class Program;

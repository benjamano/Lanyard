using Amazon.S3;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lanyard.App.Components;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Announcements;
using Lanyard.Application.Services.ApplicationRoles;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Gdpr;
using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Training;
using Lanyard.Application.Services.StaffDocuments;
using Lanyard.Application.Services.Onboarding;
using Lanyard.Application.Services.Notifications;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Application.Services.Time;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
builder.Services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();
builder.Services.AddScoped<IFileService, FileService>();

// One bucket client for the whole process. FileService is scoped and used to build its own
// AmazonS3Client per instance - a new SDK client and HTTP pipeline for every request/circuit.
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<IAmazonS3>(_ => S3StorageClientFactory.CreateFromEnvironment());
}
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
builder.Services.AddScoped<ICompanyLocationService, CompanyLocationService>();
builder.Services.AddScoped<ICurrentLocationContext, CurrentLocationContextService>();
builder.Services.AddScoped<IStaffDocumentTypeService, StaffDocumentTypeService>();
builder.Services.AddScoped<IStaffDocumentService, StaffDocumentService>();
builder.Services.AddScoped<IOnboardingService, OnboardingService>();
builder.Services.AddScoped<IStaffPositionService, StaffPositionService>();
builder.Services.AddScoped<IContractRequirementService, ContractRequirementService>();
builder.Services.AddScoped<IClockInPinService, ClockInPinService>();
builder.Services.AddScoped<ISchedulingSettingsService, SchedulingSettingsService>();
builder.Services.AddScoped<IRotaService, RotaService>();
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITerminalEphemeralTokenService, TerminalEphemeralTokenService>();
builder.Services.AddSingleton<ITerminalEventBus, TerminalEventBus>();
builder.Services.AddSingleton<ITimeOffEventBus, TimeOffEventBus>();
builder.Services.AddScoped<IClockInTerminalService, ClockInTerminalService>();
builder.Services.AddScoped<ITimeEntryService, TimeEntryService>();
builder.Services.AddScoped<ITimeOffPolicyService, TimeOffPolicyService>();
builder.Services.AddScoped<ITimeOffService, TimeOffService>();
builder.Services.AddHostedService<CourseRecurrenceHostedService>();
builder.Services.AddHostedService<TrainingDueSoonHostedService>();
builder.Services.AddHostedService<StaffDocumentExpiryReminderHostedService>();
builder.Services.AddSingleton<NotificationDispatcher>();
builder.Services.AddSingleton<INotificationDispatcher>(sp => sp.GetRequiredService<NotificationDispatcher>());
builder.Services.AddScoped<INotificationDeliverer, NotificationDeliverer>();
builder.Services.Configure<PushOptions>(builder.Configuration.GetSection("Push"));
builder.Services.AddSingleton(sp => VapidKeys.Create(
    sp.GetRequiredService<IOptions<PushOptions>>().Value,
    sp.GetRequiredService<IOptions<EmailOptions>>().Value.PublicBaseUrl,
    builder.Environment.IsDevelopment(),
    sp.GetRequiredService<ILogger<VapidKeys>>()));
builder.Services.AddHttpClient(WebPushSender.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddScoped<IPushSender, WebPushSender>();
builder.Services.AddScoped<IPushSubscriptionService, PushSubscriptionService>();
builder.Services.AddScoped<INotificationPreferenceService, NotificationPreferenceService>();
builder.Services.AddScoped<IAppInstallationService, AppInstallationService>();
builder.Services.AddHostedService<PushDeviceCleanupHostedService>();
builder.Services.AddHostedService<NotificationDeliveryHostedService>();
builder.Services.AddHostedService<ShiftReminderHostedService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<ISignalRProjectionControlHub, SignalRControlHub>();
builder.Services.AddScoped<ITimeService, TimeService>();
builder.Services.AddScoped<IDmxSceneService, DmxSceneService>();
builder.Services.AddScoped<IDmxFixtureService, DmxFixtureService>();

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
builder.Services.Configure<AutomationExecutionLogOptions>(builder.Configuration.GetSection(AutomationExecutionLogOptions.SectionName));
builder.Services.AddHostedService<AutomationExecutionRetentionHostedService>();
builder.Services.AddHostedService<IdleTriggerHostedService>();
builder.Services.AddHostedService<ScheduledTriggerHostedService>();

builder.Services.AddScoped<IClientZoneScoreboardService, ClientZoneScoreboardService>();

builder.Services.AddScoped<IAnnouncementService, AnnouncementService>();

// Kiosks report lists (cached songs, screens, devices) in single messages; a few hundred
// cached songs overflow the 32 KB default and the hub drops the connection. Raised for the
// kiosk hub only: global HubOptions would also apply to every Blazor circuit.
builder.Services.AddSignalR()
    .AddHubOptions<SignalRControlHub>(options => options.MaximumReceiveMessageSize = 256 * 1024);

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
    options.UseNpgsql(connectionString, b =>
    {
        b.MigrationsAssembly("Lanyard.Infrastructure");

        // Several read paths Include two or more collections at once (a course with its
        // sections, questions, options, attempts and answers; a program's steps with template
        // parameters and parameter values). As one SQL statement those multiply into a row per
        // combination of child rows, each repeating the parent's large text columns. Split
        // queries load each collection with its own statement instead.
        b.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
    }));

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

// Per-user date/time format from the culture cookie (see UserCultureCookie). Cookie only: the
// browser's Accept-Language must not override an explicit preference, and nothing in the app
// switches culture via the query string. Defaults to en-GB (the business is UK based).
RequestLocalizationOptions localizationOptions = new RequestLocalizationOptions()
    .SetDefaultCulture(UserCultureCookie.DefaultCulture)
    .AddSupportedCultures(UserCultureCookie.SupportedCultures)
    .AddSupportedUICultures(UserCultureCookie.SupportedCultures);
localizationOptions.RequestCultureProviders = [new CookieRequestCultureProvider()];
app.UseRequestLocalization(localizationOptions);

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

// The "ip-fixed" limiter (25/min per IP) is a brute-force guard for the auth endpoints and is
// applied on AuthController itself. It must not cover the file/audio/logo/certificate
// controllers: kiosks and staff behind one venue NAT share an IP, and a thumbnail grid or a
// kiosk warming its song cache burns through 25 requests in seconds.
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

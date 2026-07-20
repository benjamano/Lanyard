using Lanyard.App.Components;
using Lanyard.Application.Services;
using Lanyard.Application.Services.ApplicationRoles;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Application.Services.Time;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.FluentUI.AspNetCore.Components;
using System.Reflection;
using Lanyard.App.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Lanyard.Application.Services.Clients;
using Lanyard.Application.Services.VideoStreaming;
using Lanyard.App.Components.Layout;

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

// Add Razor Components with Interactive Server
builder.Services.AddRazorComponents(options => options.DetailedErrors = builder.Environment.IsDevelopment())
    .AddInteractiveServerComponents();

// Add HttpContextAccessor for accessing the current user
builder.Services.AddHttpContextAccessor();

builder.Services.AddScoped<ISecurityService, SecurityService>();
builder.Services.AddSingleton<IClientSecretValidator, ClientSecretValidator>();
builder.Services.AddScoped<IFileService, FileService>();
builder.Services.AddScoped<ApplicationRolesService>();
builder.Services.AddScoped<IPlaylistService, PlaylistService>();
builder.Services.AddScoped<IMusicService, MusicService>();
builder.Services.AddScoped<IClientService, ClientService>();
builder.Services.AddScoped<IProjectionProgramService, ProjectionProgramService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<ISignalRProjectionControlHub, SignalRControlHub>();
builder.Services.AddScoped<ITimeService, TimeService>();
builder.Services.AddScoped<IDmxSceneService, DmxSceneService>();

builder.Services.AddSingleton<ILaserGameStatusStore, LaserGameStatusStore>();
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

builder.Services.AddSingleton<AutomationEngineService>();
builder.Services.AddSingleton<IActionExecutor, MusicControlActionExecutor>();
builder.Services.AddSingleton<IActionExecutor, StartProjectionProgramActionExecutor>();
builder.Services.AddSingleton<IActionExecutor, DmxSceneControlActionExecutor>();
builder.Services.AddScoped<IAutomationRuleService, AutomationRuleService>();
builder.Services.AddScoped<IAutomationLogService, AutomationLogService>();
builder.Services.AddHostedService<AutomationEngineHostedService>();

builder.Services.AddScoped<IClientZoneScoreboardService, ClientZoneScoreboardService>();

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

// Configure Database
string? connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'DefaultConnection' is not configured. Set the "
        + "ConnectionStrings__DefaultConnection environment variable (production) or run the "
        + "local docker-compose Postgres for development.");
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

// Add FluentUI Components
builder.Services.AddFluentUIComponents(configuration =>
{
    configuration.Toast.Position = ToastPosition.TopEnd;
    configuration.Toast.Timeout = 5000;
});

builder.Services.AddSignalR();

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("ip-fixed", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 50,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
    });

    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = 429;

        await context.HttpContext.Response.WriteAsync("Too many requests.", token);
    };
});

builder.Services.AddDataProtection()
    .PersistKeysToDbContext<ApplicationDbContext>();

var app = builder.Build();

if (app.Environment.IsDevelopment() == false)
{
    // Honour X-Forwarded-Proto/For from the TLS-terminating proxy so HTTPS redirection and
    // client-IP logging see the real scheme and address. Must run before UseHttpsRedirection.
    app.UseForwardedHeaders();
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
    app.UseRateLimiter();
}

// Baseline security response headers (applied in every environment).
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

    await next();
});

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// Reject unauthenticated kiosk clients at the SignalR negotiate/handshake stage with a 401,
// rather than letting them connect and then aborting inside the hub. Returning 401 here lets the
// client distinguish "wrong secret" from a transient network drop and stop retrying immediately.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/websocket"))
    {
        IClientSecretValidator validator = context.RequestServices.GetRequiredService<IClientSecretValidator>();

        if (validator.IsConfigured && !validator.IsValid(context.Request.Query["secret"].ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            // Write a body so UseStatusCodePagesWithReExecute does not re-run the pipeline (which
            // would turn this into an antiforgery 400) — the client must receive a clean 401.
            await context.Response.WriteAsync("Invalid or missing client shared secret.");
            return;
        }
    }

    await next();
});

// Map SignalR hub for music control
app.MapHub<SignalRControlHub>("/websocket");

app.MapControllers().RequireRateLimiting("ip-fixed");

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

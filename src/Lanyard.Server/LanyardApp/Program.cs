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

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<MusicPlayerService>();
builder.Services.AddSingleton<DmxService>();
builder.Services.AddSingleton<IDmxService>(sp => sp.GetRequiredService<DmxService>());
builder.Services.AddSingleton<IDmxClientService>(sp => sp.GetRequiredService<DmxService>());

builder.Services.AddSingleton<AutomationEngineService>();
builder.Services.AddSingleton<IActionExecutor, MusicControlActionExecutor>();
builder.Services.AddScoped<IAutomationRuleService, AutomationRuleService>();
builder.Services.AddScoped<IAutomationLogService, AutomationLogService>();
builder.Services.AddHostedService<AutomationEngineHostedService>();

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
string? connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString, b => b.MigrationsAssembly("Lanyard.Infrastructure")));

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// Configure Identity with minimal settings
builder.Services.AddIdentity<UserProfile, ApplicationRole>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
    options.Password.RequireNonAlphanumeric = false;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddSignInManager()
.AddDefaultTokenProviders();

builder.Services.AddAuthorization();

// Configure cookie to persist login across sessions
builder.Services.ConfigureApplicationCookie(options =>
{
    options.ExpireTimeSpan = TimeSpan.FromDays(180);
    options.SlidingExpiration = true;
    options.LoginPath = "/HandleLogin";
    options.LogoutPath = "/HandleLogout";
    options.AccessDeniedPath = "/HandleLogin";
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
builder.Services.AddFluentUIComponents();

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
    app.UseForwardedHeaders();
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseMigrationsEndPoint();
    app.UseRateLimiter();
}

// app.UseHsts();

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
// app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

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

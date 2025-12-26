using Lanyard.App.Components;
using Lanyard.App.Data;
using Lanyard.Application.Services;
using Lanyard.Application.Services.ApplicationRoles;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyModel;
using Microsoft.FluentUI.AspNetCore.Components;

var builder = WebApplication.CreateBuilder(args);

// Add Razor Components with Interactive Server
builder.Services.AddRazorComponents(options => options.DetailedErrors = builder.Environment.IsDevelopment())
    .AddInteractiveServerComponents();

// Add HttpContextAccessor for accessing the current user
builder.Services.AddHttpContextAccessor();

// Music Services
builder.Services.AddSingleton<MusicPlayerService>();

// Other Business Services
builder.Services.AddScoped<SecurityService>();
builder.Services.AddScoped<ApplicationRolesService>();

// Configure Database
string? connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString, b => b.MigrationsAssembly("Lanyard.Infrastructure")));

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
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
    options.LoginPath = "/login";
    options.LogoutPath = "/logout";
    options.AccessDeniedPath = "/login";
});

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
builder.Services.AddFluentUIComponents(options =>
{
    options.ValidateClassNames = true;
    options.UseTooltipServiceProvider = true;
    options.HideTooltipOnCursorLeave = true;
});

// Add SignalR for real-time music control
builder.Services.AddSignalR();

var app = builder.Build();

// Seed development data (only runs in Development environment)
await DevelopmentDataSeeder.SeedDevelopmentDataAsync(app.Services, app.Environment);

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// Map SignalR hub for music control
app.MapHub<MusicControlHub>("/websocket");

app.MapControllers();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

using Lanyard.Reach.Web.Components;
using Lanyard.Reach.Web.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.FluentUI.AspNetCore.Components;

var builder = WebApplication.CreateBuilder(args);

// Per-page render modes rather than global interactivity: the marketing pages are static content
// and gain nothing from a per-visitor circuit, while the ordering flow needs WebAssembly so a
// customer's basket survives their phone dropping off the venue wifi.
builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddFluentUIComponents(options =>
{
    options.ValidateClassNames = true;
    options.UseTooltipServiceProvider = true;
    options.HideTooltipOnCursorLeave = true;
});

builder.Services.AddMemoryCache();
builder.Services.AddScoped<ITenantContext, TenantContext>();

// The one place that knows the Lanyard server exists. Customers' browsers only ever talk to this
// host; this client makes the onward call with Reach's credential attached server-side.
builder.Services.AddHttpClient<LanyardOrderingClient>(client =>
{
    string baseUrl = builder.Configuration["Lanyard:ServerUrl"]
        ?? throw new InvalidOperationException(
            "Lanyard:ServerUrl is not configured. Set Lanyard__ServerUrl to the Lanyard server's base address.");

    client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");

    string? secret = builder.Configuration["Lanyard:ReachSecret"];

    if (!string.IsNullOrWhiteSpace(secret))
    {
        client.DefaultRequestHeaders.Add("X-Lanyard-Reach-Secret", secret);
    }
});

var app = builder.Build();

// Must come first, and must stay paired with tenant resolution: behind Cloudflare the customer's
// domain only reaches us in a forwarded header, so without this every request would look like it
// arrived for the origin's own hostname and no tenant would ever resolve.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();

app.UseMiddleware<TenantResolutionMiddleware>();

app.MapOrderingBff();

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Lanyard.Reach.Web.Client._Imports).Assembly);

app.Run();

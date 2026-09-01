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
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddTransient<CustomerIdentityForwardingHandler>();

// The one place that knows the Lanyard server exists. Customers' browsers only ever talk to this
// host; this client makes the onward call with Reach's credential attached server-side.
builder.Services.AddHttpClient<LanyardOrderingClient>(client =>
{
    string baseUrl = builder.Configuration["Lanyard:ServerUrl"]
        ?? throw new InvalidOperationException(
            "Lanyard:ServerUrl is not configured. Set Lanyard__ServerUrl to the Lanyard server's base address.");

    client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");

    // Reach:SharedSecret is the same key the Lanyard server reads, and they must match: this is
    // one secret with two ends, not two settings. It previously read Lanyard:ReachSecret here
    // while the server read Reach:SharedSecret, so setting the documented key configured exactly
    // one side. Reach then started perfectly, 401'd on every ordering call, and the failure
    // reached customers as "we couldn't find this table" - blaming their QR code for a missing
    // environment variable. Lanyard:ReachSecret is still accepted so an instance already
    // deployed with the old key keeps working.
    string? secret = builder.Configuration[Lanyard.Shared.ReachApiConstants.SharedSecretConfigurationKey]
        ?? builder.Configuration["Lanyard:ReachSecret"];

    if (string.IsNullOrWhiteSpace(secret))
    {
        // Fail at startup rather than per request, and for the same reason Lanyard:ServerUrl
        // does above: without a credential every ordering call is rejected, so a Reach that
        // boots without one is not degraded, it is entirely non-functional - but looks healthy.
        if (!builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Reach:SharedSecret is not configured. Set Reach__SharedSecret to the same value as "
                + "the Lanyard server's Reach__SharedSecret; without it the ordering API rejects "
                + "every request from this site.");
        }
    }
    else
    {
        client.DefaultRequestHeaders.Add(Lanyard.Shared.ReachApiConstants.SecretHeaderName, secret);
    }
})
// Without this the ordering rate limits partition every customer in every venue into one
// window, because the Lanyard server only ever sees Reach's own address.
.AddHttpMessageHandler<CustomerIdentityForwardingHandler>();

var app = builder.Build();

// Stated once, at startup, because the two settings below are the difference between a working
// customer site and one that shows "we couldn't find this table" to everybody. Whether a secret
// is set is logged; the secret itself is not.
app.Logger.LogInformation(
    "Reach is configured to call the Lanyard server at {ServerUrl}; Reach:SharedSecret configured: {HasSecret}",
    builder.Configuration["Lanyard:ServerUrl"],
    !string.IsNullOrWhiteSpace(
        builder.Configuration[Lanyard.Shared.ReachApiConstants.SharedSecretConfigurationKey]
        ?? builder.Configuration["Lanyard:ReachSecret"]));

// Must come first, and must stay paired with tenant resolution: behind Cloudflare the customer's
// domain only reaches us in a forwarded header, so without this every request would look like it
// arrived for the origin's own hostname and no tenant would ever resolve.
ForwardedHeadersOptions forwardedHeadersOptions = new()
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,

    // Cloudflare chains several hops; the default of 1 drops the customer's real address and,
    // worse, the forwarded Host - which would leave every request looking like it arrived for
    // the origin's own hostname and no tenant resolving at all.
    ForwardLimit = null
};

// KnownProxies/KnownNetworks default to loopback only, and anything from another address is
// silently ignored - so without clearing them this call does nothing behind a real CDN. The
// trusted proxy list therefore has to be configured per environment. Empty means "trust the
// forwarded headers", which is only safe because the origin is reachable exclusively through
// the CDN; if it is ever exposed directly, set Reach:TrustedProxies and these stop being
// blanket-trusted.
forwardedHeadersOptions.KnownNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();

foreach (string proxy in builder.Configuration.GetSection("Reach:TrustedProxies").Get<string[]>() ?? [])
{
    if (System.Net.IPAddress.TryParse(proxy, out System.Net.IPAddress? parsed))
    {
        forwardedHeadersOptions.KnownProxies.Add(parsed);
    }
}

app.UseForwardedHeaders(forwardedHeadersOptions);

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

using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);

// Same-origin by construction: the ordering island talks only to the tenant's own domain, which
// proxies onward to the Lanyard server. That is what keeps the browser free of any credential
// and the server free of a CORS policy.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

await builder.Build().RunAsync();

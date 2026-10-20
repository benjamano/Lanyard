using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// What every page downloads before it can show anything - App.razor's <head> and the end of <body>.
// These pin the decisions that keep that small: nothing from another origin (third-party libraries are
// vendored under wwwroot/lib), and no script holds up the first paint except the two that must run
// before it, with Blazor starting only after every app script has run.
[TestClass]
public class FirstLoadAssetsIntegrationTests
{
    // Run before the first paint on purpose: bootCloak.js hides the page until Fluent's theme tokens are
    // live, statusBarColor.js recolours the Android status bar from the cached company colour.
    private static readonly string[] RenderBlockingAllowed = ["js/bootCloak", "js/statusBarColor"];

    private static readonly Regex ScriptTag = new("<script\\b[^>]*>", RegexOptions.IgnoreCase);
    private static readonly Regex SrcAttribute = new("\\bsrc=\"([^\"]+)\"", RegexOptions.IgnoreCase);

    private CustomWebApplicationFactory _factory = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new CustomWebApplicationFactory();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
    }

    private HttpClient Client() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<List<(string Tag, string Src)>> ExternalScriptsOnLoginAsync()
    {
        string html = await Client().GetStringAsync("/login");

        return ScriptTag.Matches(html)
            .Select(m => (Tag: m.Value, Src: SrcAttribute.Match(m.Value) is { Success: true } src ? src.Groups[1].Value : null))
            .Where(x => x.Src is not null)
            .Select(x => (x.Tag, x.Src!))
            .ToList();
    }

    [TestMethod]
    public async Task LoginPage_LoadsNothingFromAnotherOrigin()
    {
        string html = await Client().GetStringAsync("/login");

        Assert.IsFalse(html.Contains("cdn.jsdelivr.net"), "Quill/IMask are vendored under wwwroot/lib, not loaded from jsdelivr.");
        Assert.IsFalse(html.Contains("unpkg.com"), "Nothing should be loaded from unpkg.");
        Assert.IsFalse(Regex.IsMatch(html, "<(script|link)\\b[^>]*(src|href)=\"(https?:)?//", RegexOptions.IgnoreCase),
            "Every script and stylesheet should be served from Lanyard itself.");
    }

    [TestMethod]
    public async Task LoginPage_OnlyTheBootScriptsBlockRendering()
    {
        List<(string Tag, string Src)> scripts = await ExternalScriptsOnLoginAsync();

        Assert.IsTrue(scripts.Count > 0);

        foreach ((string tag, string src) in scripts)
        {
            bool deferred = Regex.IsMatch(tag, "\\s(defer|async)[\\s>=]", RegexOptions.IgnoreCase)
                || tag.Contains("type=\"module\"", StringComparison.OrdinalIgnoreCase);

            if (RenderBlockingAllowed.Any(allowed => src.Contains(allowed, StringComparison.Ordinal)))
            {
                Assert.IsFalse(deferred, $"{src} must run before the first paint, so it can't be deferred.");
                continue;
            }

            Assert.IsTrue(deferred, $"{src} blocks the first paint; give it `defer` (see App.razor's load-order note).");
        }
    }

    [TestMethod]
    public async Task LoginPage_StartsBlazorAfterEveryAppScript()
    {
        List<string> sources = (await ExternalScriptsOnLoginAsync()).Select(x => x.Src).ToList();

        int blazorWeb = sources.FindIndex(s => s.Contains("_framework/blazor.web", StringComparison.Ordinal));
        int blazorStart = sources.FindIndex(s => s.Contains("js/blazorStart", StringComparison.Ordinal));

        Assert.IsTrue(blazorWeb >= 0 && blazorStart > blazorWeb, "blazorStart.js must come after blazor.web.js.");

        // Deferred scripts run in document order, so anything a component calls must sit above
        // blazorStart.js or Blazor could call it before it exists. Only scripts nothing calls into may follow.
        string[] mayFollow = ["registerServiceWorker", "aspnetcore-browser-refresh", "ReconnectModal",
            // The rich-text editor, deliberately after Blazor (see App.razor).
            "lib/quill/", "Blazored.TextEditor/"];
        List<string> late = sources.Skip(blazorStart + 1).Where(s => !mayFollow.Any(m => s.Contains(m, StringComparison.Ordinal))).ToList();

        Assert.AreEqual(0, late.Count, $"These load after Blazor starts: {string.Join(", ", late)}");
    }

    [TestMethod]
    public async Task ContentSecurityPolicy_AllowsOnlyLanyardForScriptsAndStyles()
    {
        HttpResponseMessage response = await Client().GetAsync("/login");
        string csp = string.Join(" ", response.Headers.GetValues("Content-Security-Policy"));

        StringAssert.Contains(csp, "script-src 'self' 'unsafe-inline';");
        StringAssert.Contains(csp, "style-src 'self' 'unsafe-inline';");
        Assert.IsFalse(csp.Contains("jsdelivr"), csp);
    }

    [TestMethod]
    [DataRow("/lib/quill/2.0.3/quill.js")]
    [DataRow("/lib/quill/2.0.3/quill.core.css")]
    [DataRow("/lib/quill/2.0.3/quill.snow.css")]
    [DataRow("/lib/imask/7.6.1/imask.min.js")]
    public async Task VendoredLibraries_AreServed(string path)
    {
        HttpResponseMessage response = await Client().GetAsync(path);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
    }
}

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
    private static readonly Regex StylesheetHref = new("<link\\b[^>]*\\bhref=\"([^\"]+\\.css)\"", RegexOptions.IgnoreCase);

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
        string[] mayFollow = ["registerServiceWorker", "aspnetcore-browser-refresh", "ReconnectModal"];
        List<string> late = sources.Skip(blazorStart + 1).Where(s => !mayFollow.Any(m => s.Contains(m, StringComparison.Ordinal))).ToList();

        Assert.AreEqual(0, late.Count, $"These load after Blazor starts: {string.Join(", ", late)}");
    }

    // Each of these is used by one area only, so the component that needs it imports it (or, for the
    // editor, QuillGate loads it) - none of it should be on a page that doesn't edit rich text or
    // stream video. Only <script>/<link> tags count: the import map lists every module (so imports
    // resolve to fingerprinted files), but listing one doesn't download it.
    [TestMethod]
    [DataRow("quill.js")]
    [DataRow("quill.snow")]
    [DataRow("Blazored.TextEditor/")]
    [DataRow("courseImageEditor")]
    [DataRow("kioskWidgetEditor")]
    [DataRow("trainingScrollGate")]
    [DataRow("courseWalkthrough")]
    [DataRow("dmxKeybinds")]
    [DataRow("video-stream")]
    [DataRow("liveCapture")]
    public async Task LoginPage_DoesNotLoadSingleAreaScripts(string asset)
    {
        string html = await Client().GetStringAsync("/login");
        List<string> loaded = [.. (await ExternalScriptsOnLoginAsync()).Select(x => x.Src), .. StylesheetHref.Matches(html).Select(m => m.Groups[1].Value)];

        Assert.IsFalse(loaded.Any(src => src.Contains(asset, StringComparison.Ordinal)), $"{asset} should load on demand, not on every page.");
    }

    [TestMethod]
    public async Task LoginPage_KeepsQuillContentStylesForReadOnlyRichText()
    {
        string html = await Client().GetStringAsync("/login");

        Assert.IsTrue(StylesheetHref.Matches(html).Any(m => m.Groups[1].Value.Contains("lib/quill/2.0.3/quill.core.", StringComparison.Ordinal)));
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

    // The vendored libraries and the modules pages import on demand.
    [TestMethod]
    [DataRow("/lib/quill/2.0.3/quill.js")]
    [DataRow("/lib/quill/2.0.3/quill.core.css")]
    [DataRow("/lib/quill/2.0.3/quill.snow.css")]
    [DataRow("/lib/imask/7.6.1/imask.min.js")]
    [DataRow("/js/quillLoader.js")]
    [DataRow("/js/liveCapture.js")]
    [DataRow("/_content/Blazored.TextEditor/Blazored-BlazorQuill.js")]
    [DataRow("/_content/Blazored.TextEditor/quill-blot-formatter.min.js")]
    public async Task VendoredLibraries_AreServed(string path)
    {
        HttpResponseMessage response = await Client().GetAsync(path);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
    }
}

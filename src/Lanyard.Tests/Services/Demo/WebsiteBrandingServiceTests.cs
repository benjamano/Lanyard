using System.Net;
using System.Text;
using Lanyard.Application.Services.Demo;
using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Demo;

// The demo's "get my branding from my website": what it reads from a site, and - since anyone on
// the public demo can type any URL - that the server can't be pointed at private addresses.
[TestClass]
public class WebsiteBrandingServiceTests
{
    // A tiny fake website: path -> (content type, body).
    private sealed class FakeSite(Dictionary<string, (string Type, byte[] Body)> pages) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            Requested.Add(path);

            HttpResponseMessage response = pages.TryGetValue(path, out (string Type, byte[] Body) page)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(page.Body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };

            if (page.Type is not null)
            {
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(page.Type);
            }

            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private static readonly byte[] PngBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static WebsiteBrandingService ServiceFor(HttpMessageHandler handler)
    {
        Mock<IHttpClientFactory> factory = new();
        factory.Setup(x => x.CreateClient(WebsiteBrandingService.HttpClientName)).Returns(() => new HttpClient(handler, disposeHandler: false));
        return new WebsiteBrandingService(factory.Object, NullLogger<WebsiteBrandingService>.Instance);
    }

    private static (string, byte[]) Html(string html) => ("text/html", Encoding.UTF8.GetBytes(html));

    [TestMethod]
    public async Task ReadsNameColourAndTheBestIconFromTheSite()
    {
        FakeSite site = new(new()
        {
            ["/"] = Html("""
                <html><head>
                  <title>Home | Acme Leisure</title>
                  <meta property="og:site_name" content="Acme Leisure">
                  <meta name="theme-color" content="#0e7c86">
                  <link rel="icon" href="/favicon-32.png" sizes="32x32">
                  <link rel="apple-touch-icon" href="/apple-touch-icon.png" sizes="180x180">
                </head><body></body></html>
                """),
            ["/apple-touch-icon.png"] = ("image/png", PngBytes),
            ["/favicon-32.png"] = ("image/png", PngBytes),
        });

        Result<WebsiteBranding> result = await ServiceFor(site).LookupAsync("acme.example.com");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("Acme Leisure", result.Data!.Name);
        Assert.AreEqual("#0E7C86", result.Data.ColorHex);
        StringAssert.StartsWith(result.Data.LogoDataUrl, "data:image/png;base64,");
        Assert.AreEqual("https://acme.example.com", result.Data.SiteUrl);
        CollectionAssert.Contains(site.Requested, "/apple-touch-icon.png", "The big app icon beats the 32px favicon.");
        CollectionAssert.DoesNotContain(site.Requested, "/favicon-32.png");
    }

    [TestMethod]
    public async Task FallsBackToTheManifestAndTheTitle_AndIgnoresAWhiteThemeColour()
    {
        FakeSite site = new(new()
        {
            ["/"] = Html("""
                <html><head>
                  <title>Welcome - Harbour Bowl</title>
                  <meta name="theme-color" content="#ffffff">
                  <link rel="manifest" href="/site.webmanifest">
                </head></html>
                """),
            ["/site.webmanifest"] = ("application/manifest+json", Encoding.UTF8.GetBytes("""
                { "theme_color": "rgb(200, 16, 46)", "icons": [ { "src": "/icon-512.png", "sizes": "512x512" } ] }
                """)),
            ["/icon-512.png"] = ("image/png", PngBytes),
        });

        Result<WebsiteBranding> result = await ServiceFor(site).LookupAsync("https://bowl.example.com/");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("Harbour Bowl", result.Data!.Name, "The title's generic 'Welcome' part is dropped.");
        Assert.AreEqual("#C8102E", result.Data.ColorHex, "White is a plain browser bar, not a brand colour.");
        Assert.IsNotNull(result.Data.LogoDataUrl);
    }

    [TestMethod]
    public async Task ASiteThatBlocksBots_SaysSo()
    {
        HttpMessageHandler blocked = new BlockingSite();

        Result<WebsiteBranding> result = await ServiceFor(blocked).LookupAsync("protected.example.com");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "doesn't let automated visitors");
    }

    private sealed class BlockingSite : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<html>Checking your browser</html>", Encoding.UTF8, "text/html"), RequestMessage = request });
    }

    [TestMethod]
    public async Task AnythingThatIsntAWebPage_IsRefused()
    {
        FakeSite site = new(new() { ["/"] = ("application/pdf", [1, 2, 3]) });

        Result<WebsiteBranding> result = await ServiceFor(site).LookupAsync("files.example.com");

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("localhost")]
    [DataRow("javascript:alert(1)")]
    [DataRow("ftp://example.com")]
    [DataRow("https://user:pass@example.com")]
    [DataRow("https://example.com:8080")]
    public void OddAddresses_AreRejectedBeforeAnythingIsFetched(string input)
    {
        Assert.IsFalse(WebsiteBrandingService.TryNormaliseUrl(input, out _));
    }

    [TestMethod]
    public void BareDomains_GetHttps()
    {
        Assert.IsTrue(WebsiteBrandingService.TryNormaliseUrl("acme.co.uk", out Uri? url));
        Assert.AreEqual("https://acme.co.uk/", url!.ToString());
    }

    [TestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("10.1.2.3")]
    [DataRow("172.20.0.5")]
    [DataRow("192.168.1.10")]
    [DataRow("169.254.169.254")]
    [DataRow("100.64.0.1")]
    [DataRow("0.0.0.0")]
    [DataRow("::1")]
    [DataRow("fe80::1")]
    [DataRow("fd12:3456:789a::1")]
    [DataRow("::ffff:10.0.0.1")]
    public void PrivateAndInternalAddresses_AreNotPublic(string address)
    {
        Assert.IsFalse(PublicAddressGuard.IsPublic(IPAddress.Parse(address)), address);
    }

    [TestMethod]
    [DataRow("93.184.215.14")]
    [DataRow("8.8.8.8")]
    [DataRow("2606:4700:4700::1111")]
    public void OrdinaryInternetAddresses_ArePublic(string address)
    {
        Assert.IsTrue(PublicAddressGuard.IsPublic(IPAddress.Parse(address)), address);
    }

    // Through the real guarded handler: these are refused at connect time, before any request is sent.
    [TestMethod]
    [DataRow("http://127.0.0.1/")]
    [DataRow("http://169.254.169.254/latest/meta-data/")]
    [DataRow("http://localhost.localdomain/")]
    public async Task TheRealHandler_WontConnectToInternalAddresses(string url)
    {
        using SocketsHttpHandler handler = PublicAddressGuard.CreateHandler();

        Result<WebsiteBranding> result = await ServiceFor(handler).LookupAsync(url);

        Assert.IsFalse(result.IsSuccess);
    }
}

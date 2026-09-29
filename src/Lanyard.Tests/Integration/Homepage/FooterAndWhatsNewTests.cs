using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration.Homepage;

[TestClass]
public class FooterAndWhatsNewTests
{
    private static HttpClient AnonymousClient(CustomWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [TestMethod]
    public async Task Homepage_HasAFooterWithWhatsNewAndPrivacy_AndOrganizationStructuredData()
    {
        using CustomWebApplicationFactory factory = new();
        string html = await AnonymousClient(factory).GetStringAsync("/");

        StringAssert.Contains(html, "<footer class=\"landing-footer\"");
        StringAssert.Contains(html, "href=\"/whats-new\"");
        StringAssert.Contains(html, "href=\"/privacy\"");

        List<string> types = [];
        foreach (Match script in Regex.Matches(html, "<script type=\"application/ld\\+json\">(.*?)</script>", RegexOptions.Singleline))
        {
            using JsonDocument json = JsonDocument.Parse(script.Groups[1].Value);
            if (json.RootElement.TryGetProperty("@graph", out JsonElement graph))
            {
                types.AddRange(graph.EnumerateArray().Select(x => x.GetProperty("@type").GetString()!));
            }
        }

        CollectionAssert.Contains(types, "Organization");
        CollectionAssert.Contains(types, "SoftwareApplication");
    }

    [TestMethod]
    public async Task WhatsNew_IsPublic_ListsTheReleases_AndStaysOutOfSearchResults()
    {
        using CustomWebApplicationFactory factory = new();
        HttpResponseMessage response = await AnonymousClient(factory).GetAsync("/whats-new");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "Anonymous visitors see the page rather than a redirect to login.");
        string html = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "What's new in Lanyard");
        StringAssert.Contains(html, "v1.4.11");
        StringAssert.Contains(response.Headers.GetValues("X-Robots-Tag").Single(), "noindex");
    }
}

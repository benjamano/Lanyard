using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration.Homepage;

[TestClass]
public class ProductTourSectionTests
{
    [TestMethod]
    public async Task Homepage_ShowsProductScreenshots_ThatAllLoadAndAreDescribed()
    {
        using CustomWebApplicationFactory factory = new();
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        string html = await client.GetStringAsync("/");

        StringAssert.Contains(html, "See it in action");

        MatchCollection images = Regex.Matches(html, "<img src=\"(/images/landing/[^\"]+)\"[^>]*alt=\"([^\"]*)\"");
        Assert.IsTrue(images.Count >= 4, $"Found {images.Count} screenshots.");

        foreach (Match image in images)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(image.Groups[2].Value), $"{image.Groups[1].Value} has alt text.");

            HttpResponseMessage response = await client.GetAsync(image.Groups[1].Value);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, image.Groups[1].Value);
            Assert.AreEqual("image/webp", response.Content.Headers.ContentType?.MediaType, image.Groups[1].Value);
        }
    }
}

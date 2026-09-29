using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration.Homepage;

[TestClass]
public class FaqSectionTests
{
    [TestMethod]
    public async Task Homepage_AnswersCommonQuestions_AndPublishesThemAsFaqStructuredData()
    {
        using CustomWebApplicationFactory factory = new();
        string html = await factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).GetStringAsync("/");

        StringAssert.Contains(html, "Questions venues ask");
        StringAssert.Contains(html, "What do we need at the venue?");

        Match script = Regex.Match(html, "<script type=\"application/ld\\+json\">(.*?)</script>", RegexOptions.Singleline);
        Assert.IsTrue(script.Success, "The FAQ is published as structured data.");

        using JsonDocument json = JsonDocument.Parse(script.Groups[1].Value);
        Assert.AreEqual("FAQPage", json.RootElement.GetProperty("@type").GetString());
        Assert.IsTrue(json.RootElement.GetProperty("mainEntity").GetArrayLength() >= 7);
    }
}

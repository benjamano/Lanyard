using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration.Homepage;

[TestClass]
public class GettingStartedSectionTests
{
    [TestMethod]
    public async Task Homepage_ShowsTheThreeStepsToGetStarted()
    {
        using CustomWebApplicationFactory factory = new();
        string html = await factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).GetStringAsync("/");

        StringAssert.Contains(html, "How to get started");
        StringAssert.Contains(html, "We set up your company");
        StringAssert.Contains(html, "Install the venue app");
    }
}

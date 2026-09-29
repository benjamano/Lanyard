using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration.Homepage;

[TestClass]
public class VenueTypesSectionTests
{
    [TestMethod]
    public async Task Homepage_ListsTheVenueTypesLanyardIsFor()
    {
        using CustomWebApplicationFactory factory = new();
        string html = await factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).GetStringAsync("/");

        StringAssert.Contains(html, "Built for venues like yours");
        StringAssert.Contains(html, "Laser tag arenas");
        StringAssert.Contains(html, "Trampoline parks");
    }
}

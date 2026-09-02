using System.Net;
using System.Net.Http.Json;
using Lanyard.Tests.Integration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Kitchen;

/// <summary>
/// Who may read and change a venue's kitchen queue through the public API.
///
/// This controller once accepted any authenticated cookie, with no role and no venue check, so a
/// signed-in member of staff at one company could read another company's live tickets, customer
/// notes and takings - and change their order statuses - by putting a different locationId in the
/// URL. These run through the real pipeline because the hole was in the authorisation glue, which
/// a service-level test cannot see.
/// </summary>
[TestClass]
public class KitchenControllerAuthorizationTests
{
    [TestMethod]
    public async Task Queue_IsRefusedWithoutAnyCredential()
    {
        using CustomWebApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/kitchen/1/queue");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Stats_IsRefusedWithoutAnyCredential()
    {
        using CustomWebApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/kitchen/1/stats");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task MenuItems_IsRefusedWithoutAnyCredential()
    {
        using CustomWebApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/kitchen/1/menu-items");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Keyed by order rather than location, so the venue has to be resolved before the caller can
    /// be authorised against it. An unknown order is refused rather than reported as missing, so
    /// this cannot be walked to discover which order ids exist.
    /// </summary>
    [TestMethod]
    public async Task SetStatus_IsRefusedWithoutAnyCredential()
    {
        using CustomWebApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/kitchen/orders/1/status", new { status = 1 });

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task SetAvailability_IsRefusedWithoutAnyCredential()
    {
        using CustomWebApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/kitchen/menu-items/1/availability", new { isAvailable = false });

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

using System.Net;
using System.Net.Http.Json;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

[TestClass]
public class RateLimitIntegrationTests
{
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

    [TestMethod]
    public async Task AnonymousUser_ExceedingRateLimit_For_LoginEndpoint_IsRejected()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        for (int i = 0; i < 25; i++)
        {
            HttpResponseMessage response = await client.PostAsync("/api/Auth/login", null);
            Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode, $"Expected Unsupported Media Type on request {i + 1}, got {(int)response.StatusCode}.");
        }

        HttpResponseMessage rateLimitedResponse = await client.PostAsync("/api/Auth/login", null);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, rateLimitedResponse.StatusCode, $"Expected Too Many Requests, got {(int)rateLimitedResponse.StatusCode}.");
    }

    /// <summary>
    /// The ordering API must not inherit the app-wide per-IP limit.
    ///
    /// Reach proxies every customer's request server-side, so the Lanyard server sees one
    /// address for an entire venue - 25 requests a minute shared between every diner, which is
    /// roughly two people before hard 429s.
    ///
    /// This is an integration test rather than a unit test on purpose: whether an
    /// [EnableRateLimiting] attribute actually takes effect depends on how the endpoint was
    /// mapped, which is invisible from the controller source. Reading the code produced the
    /// wrong answer once already.
    /// </summary>
    [TestMethod]
    public async Task OrderingEndpoints_AreNotSubjectToThePerIpLimit()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Comfortably past ip-fixed's 25/minute. The tenant does not exist, so 404 is the
        // expected answer - what matters is that it never becomes 429.
        for (int i = 0; i < 60; i++)
        {
            HttpResponseMessage response = await client.GetAsync("/api/ordering/tenants/by-host/not-a-real-tenant.example");

            Assert.AreNotEqual(
                HttpStatusCode.TooManyRequests,
                response.StatusCode,
                $"Ordering request {i + 1} was rate limited, so the ordering policy is not being applied.");
        }
    }

    /// <summary>
    /// The ordering write limit is real, just far higher than the per-IP one. Proves the
    /// ordering policies are attached rather than rate limiting simply being switched off.
    /// </summary>
    [TestMethod]
    public async Task OrderingWrites_AreStillRateLimited()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpStatusCode? limited = null;

        // The write policy permits 10 per fixed minute. Enough iterations that the limit is hit
        // even if the loop straddles a window boundary and the counter resets part-way: 25
        // requests leave at least 13 in one window whichever side the boundary falls. A tighter
        // loop passed in isolation and failed on a loaded machine.
        for (int i = 0; i < 25 && limited is null; i++)
        {
            HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/ordering/orders?companyId=1",
                new { tableToken = "nope", lines = Array.Empty<object>() });

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response.StatusCode;
            }
        }

        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited, "Order creation should be rate limited by the ordering write policy.");
    }
}
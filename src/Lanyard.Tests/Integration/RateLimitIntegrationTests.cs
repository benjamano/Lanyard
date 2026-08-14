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
}
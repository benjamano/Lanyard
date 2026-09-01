extern alias reach;

using System.Net;
using System.Text;
using reach::Lanyard.Reach.Web.Services;
using Lanyard.Shared.DTO;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Reach;

/// <summary>
/// How Reach interprets a failed call to the Lanyard ordering API.
///
/// This exists because of a real bug found under load: every non-success response was collapsed
/// into null, and the BFF turned null into 404. A customer who merely tapped too fast was told
/// their table did not exist, so they went and found a member of staff about a QR code that was
/// working perfectly. The outcome has to survive the round trip.
/// </summary>
[TestClass]
public class LanyardOrderingClientTests
{
    private sealed class StubHandler(HttpStatusCode statusCode, string body = "") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    private static LanyardOrderingClient BuildClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://lanyard.test/") },
            new Mock<ILogger<LanyardOrderingClient>>().Object);

    [TestMethod]
    public async Task Get_ReportsRateLimitedSeparatelyFromNotFound()
    {
        LanyardOrderingClient client = BuildClient(new StubHandler(HttpStatusCode.TooManyRequests));

        OrderingApiResult<TableResolutionDto> result =
            await client.GetAsync<TableResolutionDto>("api/ordering/tables/abc", CancellationToken.None);

        Assert.AreEqual(OrderingApiOutcome.RateLimited, result.Outcome);
        Assert.IsFalse(result.IsOk);
    }

    [TestMethod]
    public async Task Get_ReportsNotFoundForAGenuinelyMissingTable()
    {
        LanyardOrderingClient client = BuildClient(new StubHandler(HttpStatusCode.NotFound));

        OrderingApiResult<TableResolutionDto> result =
            await client.GetAsync<TableResolutionDto>("api/ordering/tables/abc", CancellationToken.None);

        Assert.AreEqual(OrderingApiOutcome.NotFound, result.Outcome);
    }

    /// <summary>
    /// The one that cost a staging evening. Reach read Lanyard:ReachSecret while the server read
    /// Reach:SharedSecret, so setting the documented key configured exactly one side; the server
    /// then 401'd every call and customers were told their table code was wrong.
    /// </summary>
    [TestMethod]
    public async Task Get_ReportsARefusedCredentialSeparatelyFromNotFound()
    {
        LanyardOrderingClient client = BuildClient(new StubHandler(HttpStatusCode.Unauthorized));

        OrderingApiResult<TableResolutionDto> result =
            await client.GetAsync<TableResolutionDto>("api/ordering/tables/abc", CancellationToken.None);

        Assert.AreEqual(OrderingApiOutcome.Unauthorized, result.Outcome);
        Assert.AreNotEqual(OrderingApiOutcome.NotFound, result.Outcome);
    }

    [TestMethod]
    public async Task Get_ReportsAServerErrorAsUnavailableRatherThanMissing()
    {
        LanyardOrderingClient client = BuildClient(new StubHandler(HttpStatusCode.InternalServerError));

        OrderingApiResult<TableResolutionDto> result =
            await client.GetAsync<TableResolutionDto>("api/ordering/tables/abc", CancellationToken.None);

        Assert.AreEqual(OrderingApiOutcome.Unavailable, result.Outcome);
    }

    /// <summary>
    /// The Lanyard server being down entirely is still not the customer's table being wrong.
    /// </summary>
    [TestMethod]
    public async Task Get_ReportsAnUnreachableServerAsUnavailable()
    {
        LanyardOrderingClient client = BuildClient(new ThrowingHandler());

        OrderingApiResult<TableResolutionDto> result =
            await client.GetAsync<TableResolutionDto>("api/ordering/tables/abc", CancellationToken.None);

        Assert.AreEqual(OrderingApiOutcome.Unavailable, result.Outcome);
    }

    [TestMethod]
    public async Task Get_ReturnsTheDeserialisedBodyOnSuccess()
    {
        LanyardOrderingClient client = BuildClient(new StubHandler(
            HttpStatusCode.OK,
            """{"companyId":1,"locationId":7,"locationName":"Play2Day Ipswich","tableLabel":"Table 4","orderingEnabled":true}"""));

        OrderingApiResult<TableResolutionDto> result =
            await client.GetAsync<TableResolutionDto>("api/ordering/tables/abc", CancellationToken.None);

        Assert.IsTrue(result.IsOk);
        Assert.AreEqual(7, result.Value!.LocationId);
        Assert.AreEqual("Table 4", result.Value.TableLabel);
    }
}

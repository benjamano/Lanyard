using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lanyard.Shared.Contracts;
using Lanyard.Shared.Kiosk;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.Kiosk;

[TestClass]
public class KioskEnrolmentClientTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid RequestId = Guid.NewGuid();

    private sealed class MemoryStore : IKioskCredentialStore
    {
        public string? Value { get; set; }
        public string? Load() => Value;
        public void Save(string credential) => Value = credential;
        public void Delete() => Value = null;
    }

    private sealed class RecordingDisplay : IKioskPairingDisplay
    {
        public List<string> Codes { get; } = [];
        public List<string> Statuses { get; } = [];
        public bool Closed { get; private set; }
        public void ShowCode(string pairingCode, DateTime expiresUtc) => Codes.Add(pairingCode);
        public void ShowStatus(string message) => Statuses.Add(message);
        public void Close() => Closed = true;
    }

    // Answers each request with the next canned response for its path, and records what was sent.
    private sealed class ScriptedServer : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<Func<HttpRequestMessage, HttpResponseMessage>>> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public ScriptedServer On(string path, Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            if (!_responses.TryGetValue(path, out Queue<Func<HttpRequestMessage, HttpResponseMessage>>? queue))
            {
                queue = new();
                _responses[path] = queue;
            }

            queue.Enqueue(respond);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            string path = request.RequestUri!.AbsolutePath.TrimStart('/');

            if (_responses.TryGetValue(path, out Queue<Func<HttpRequestMessage, HttpResponseMessage>>? queue) && queue.Count > 0)
            {
                return Task.FromResult(queue.Count == 1 ? queue.Peek()(request) : queue.Dequeue()(request));
            }

            throw new HttpRequestException("No route to host");
        }
    }

    private static HttpResponseMessage Ok<T>(T data) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new { isSuccess = true, data }, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };

    private static KioskEnrolmentStartedDTO Started(string code = "ABCD-EF12") => new(RequestId, code, "poll-token", DateTime.UtcNow.AddMinutes(15), 5);

    private static (KioskEnrolmentClient Client, MemoryStore Store, RecordingDisplay Display, ScriptedServer Server) Create(Action<ScriptedServer> script)
    {
        ScriptedServer server = new();
        script(server);

        MemoryStore store = new();
        RecordingDisplay display = new();
        HttpClient http = new(server) { BaseAddress = new Uri("https://lanyard.test/") };

        return (new KioskEnrolmentClient(http, store, display, _ => { }, (_, _) => Task.CompletedTask), store, display, server);
    }

    [TestMethod]
    public async Task UsesTheSavedCredential_WithoutAskingTheServer()
    {
        (KioskEnrolmentClient client, MemoryStore store, _, ScriptedServer server) = Create(_ => { });
        store.Value = "lk1.saved";

        string credential = await client.EnsureCredentialAsync(ClientId, "PC", "1.0.43", legacySecret: "shared");

        Assert.AreEqual("lk1.saved", credential);
        Assert.IsEmpty(server.Requests);
    }

    [TestMethod]
    public async Task SwapsTheSharedSecret_ForAKnownKiosk()
    {
        (KioskEnrolmentClient client, MemoryStore store, RecordingDisplay display, ScriptedServer server) = Create(s => s
            .On(KioskEnrolmentRoutes.LegacyExchange, _ => Ok(new KioskCredentialIssuedDTO(ClientId, "lk1.exchanged"))));

        string credential = await client.EnsureCredentialAsync(ClientId, "PC", "1.0.43", legacySecret: "shared");

        Assert.AreEqual("lk1.exchanged", credential);
        Assert.AreEqual("lk1.exchanged", store.Value);
        Assert.IsEmpty(display.Codes, "A kiosk that could swap its secret shouldn't have shown a pairing code.");
        Assert.AreEqual("shared", server.Requests[0].Headers.GetValues(ClientConnectionQuery.SecretHeader).Single());
        StringAssert.Contains(server.Requests[0].RequestUri!.Query, ClientId.ToString());
    }

    [TestMethod]
    public async Task PairsWithACode_WhenTheSwapIsRefused()
    {
        (KioskEnrolmentClient client, MemoryStore store, RecordingDisplay display, ScriptedServer server) = Create(s => s
            .On(KioskEnrolmentRoutes.LegacyExchange, _ => new HttpResponseMessage(HttpStatusCode.BadRequest))
            .On(KioskEnrolmentRoutes.Requests, _ => Ok(Started()))
            .On(KioskEnrolmentRoutes.Request(RequestId), _ => Ok(new KioskEnrolmentStatusDTO(KioskEnrolmentState.Pending, null, null)))
            .On(KioskEnrolmentRoutes.Request(RequestId), _ => Ok(new KioskEnrolmentStatusDTO(KioskEnrolmentState.Approved, ClientId, "lk1.paired"))));

        string credential = await client.EnsureCredentialAsync(ClientId, "PC", "1.0.43", legacySecret: "shared");

        Assert.AreEqual("lk1.paired", credential);
        Assert.AreEqual("lk1.paired", store.Value);
        CollectionAssert.AreEqual(new[] { "ABCD-EF12" }, display.Codes);
        Assert.IsTrue(display.Closed, "The pairing screen should close once paired.");

        HttpRequestMessage poll = server.Requests.Last();
        Assert.AreEqual("poll-token", poll.Headers.GetValues(KioskEnrolmentRoutes.EnrolmentTokenHeader).Single());
    }

    [TestMethod]
    public async Task GetsANewCode_WhenTheOldOneExpiresOrIsRejected()
    {
        (KioskEnrolmentClient client, _, RecordingDisplay display, _) = Create(s => s
            .On(KioskEnrolmentRoutes.Requests, _ => Ok(Started("AAAA-AAAA")))
            .On(KioskEnrolmentRoutes.Requests, _ => Ok(Started("BBBB-BBBB")))
            .On(KioskEnrolmentRoutes.Requests, _ => Ok(Started("CCCC-CCCC")))
            .On(KioskEnrolmentRoutes.Request(RequestId), _ => Ok(new KioskEnrolmentStatusDTO(KioskEnrolmentState.Expired, null, null)))
            .On(KioskEnrolmentRoutes.Request(RequestId), _ => Ok(new KioskEnrolmentStatusDTO(KioskEnrolmentState.Rejected, null, null)))
            .On(KioskEnrolmentRoutes.Request(RequestId), _ => Ok(new KioskEnrolmentStatusDTO(KioskEnrolmentState.Approved, ClientId, "lk1.third-time"))));

        string credential = await client.EnsureCredentialAsync(ClientId, "PC", null, legacySecret: null);

        Assert.AreEqual("lk1.third-time", credential);
        CollectionAssert.AreEqual(new[] { "AAAA-AAAA", "BBBB-BBBB", "CCCC-CCCC" }, display.Codes);
        Assert.IsTrue(display.Statuses.Any(x => x.Contains("turned this kiosk down")));
    }

    [TestMethod]
    public async Task KeepsTrying_WhileTheServerIsUnreachable()
    {
        int attempts = 0;

        ScriptedServer server = new();
        server.On(KioskEnrolmentRoutes.Requests, _ => ++attempts < 3 ? throw new HttpRequestException("down") : Ok(Started()));
        server.On(KioskEnrolmentRoutes.Request(RequestId), _ => Ok(new KioskEnrolmentStatusDTO(KioskEnrolmentState.Approved, ClientId, "lk1.eventually")));

        RecordingDisplay display = new();
        KioskEnrolmentClient client = new(new HttpClient(server) { BaseAddress = new Uri("https://lanyard.test/") }, new MemoryStore(), display, _ => { }, (_, _) => Task.CompletedTask);

        string credential = await client.EnsureCredentialAsync(ClientId, "PC", null, legacySecret: null);

        Assert.AreEqual("lk1.eventually", credential);
        Assert.AreEqual(3, attempts);
        Assert.IsTrue(display.Statuses.Any(x => x.Contains("Can't reach")));
    }

    [TestMethod]
    public async Task IsCredentialRejected_OnlyWhenTheServerSaysInvalidToken()
    {
        static HttpClient ServerAnswering(Func<HttpResponseMessage> answer)
        {
            ScriptedServer server = new();
            server.On(ClientUpdateRoutes.Feed(ClientId), _ => answer());
            return new HttpClient(server) { BaseAddress = new Uri("https://lanyard.test/") };
        }

        HttpResponseMessage InvalidToken()
        {
            HttpResponseMessage response = new(HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.ParseAdd("Bearer error=\"invalid_token\"");
            return response;
        }

        Assert.IsTrue(await KioskEnrolmentClient.IsCredentialRejectedAsync(ServerAnswering(InvalidToken), ClientId, "lk1.x"));
        Assert.IsFalse(await KioskEnrolmentClient.IsCredentialRejectedAsync(ServerAnswering(() => new HttpResponseMessage(HttpStatusCode.Unauthorized)), ClientId, "lk1.x"));
        Assert.IsFalse(await KioskEnrolmentClient.IsCredentialRejectedAsync(ServerAnswering(() => new HttpResponseMessage(HttpStatusCode.InternalServerError)), ClientId, "lk1.x"));
        Assert.IsFalse(await KioskEnrolmentClient.IsCredentialRejectedAsync(ServerAnswering(() => throw new HttpRequestException("down")), ClientId, "lk1.x"));
    }
}

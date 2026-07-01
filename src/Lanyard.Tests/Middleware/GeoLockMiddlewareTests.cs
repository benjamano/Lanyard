using Lanyard.App.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;

namespace Lanyard.Tests.Middleware
{
    [TestClass]
    public class GeoLockMiddlewareTests
    {
        // Builds config pointing at a non-existent DB file by default, simulating missing database.
        private static IConfiguration BuildConfig(
            string dbPath = "nonexistent.mmdb",
            string[]? allowedCountries = null,
            string[]? allowedIPs = null)
        {
            var dict = new Dictionary<string, string?>
            {
                ["GeoLock:Enabled"] = "true",
                ["GeoLock:DatabasePath"] = dbPath,
                ["GeoLock:CacheDurationMinutes"] = "60",
            };

            string[] countries = allowedCountries ?? ["GB"];
            for (int i = 0; i < countries.Length; i++)
                dict[$"GeoLock:AllowedCountries:{i}"] = countries[i];

            if (allowedIPs != null)
                for (int i = 0; i < allowedIPs.Length; i++)
                    dict[$"GeoLock:AllowedIPs:{i}"] = allowedIPs[i];

            return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        }

        private static DefaultHttpContext BuildContext(string? ip, string path = "/")
        {
            var context = new DefaultHttpContext();
            if (ip != null)
                context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            context.Request.Path = path;
            return context;
        }

        private static GeoLockMiddleware BuildMiddleware(RequestDelegate next, IConfiguration? config = null)
        {
            return new GeoLockMiddleware(
                next,
                config ?? BuildConfig(),
                NullLogger<GeoLockMiddleware>.Instance,
                new MemoryCache(new MemoryCacheOptions()));
        }

        // --- Missing database ---

        [TestMethod]
        public async Task MissingDatabaseFile_AllowsAllRequests()
        {
            bool nextCalled = false;
            GeoLockMiddleware middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            await middleware.InvokeAsync(BuildContext("1.2.3.4"));

            Assert.IsTrue(nextCalled, "When the database file is missing the middleware should allow all traffic.");
        }

        // --- Path bypass (checked before IP, so these work regardless of DB state) ---

        [TestMethod]
        public async Task BlockedPagePath_BypassesGeoCheck()
        {
            bool nextCalled = false;
            GeoLockMiddleware middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            await middleware.InvokeAsync(BuildContext("1.2.3.4", "/blocked"));

            Assert.IsTrue(nextCalled, "/blocked must always be reachable so non-UK users see the error page.");
        }

        [TestMethod]
        public async Task FrameworkAssetsPath_BypassesGeoCheck()
        {
            bool nextCalled = false;
            GeoLockMiddleware middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            await middleware.InvokeAsync(BuildContext("1.2.3.4", "/_framework/blazor.server.js"));

            Assert.IsTrue(nextCalled, "Blazor framework assets must load for the blocked page to render.");
        }

        [TestMethod]
        public async Task BlazorHubPath_BypassesGeoCheck()
        {
            bool nextCalled = false;
            GeoLockMiddleware middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            await middleware.InvokeAsync(BuildContext("1.2.3.4", "/_blazor"));

            Assert.IsTrue(nextCalled);
        }

        [TestMethod]
        public async Task FaviconPath_BypassesGeoCheck()
        {
            bool nextCalled = false;
            GeoLockMiddleware middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            await middleware.InvokeAsync(BuildContext("1.2.3.4", "/favicon.ico"));

            Assert.IsTrue(nextCalled);
        }

        // --- IP bypass (loopback, private, link-local) ---
        // These use no-DB config so all reach _next via the "no reader" early exit.
        // The private-range logic is also exercised by the IPv4-mapped tests below,
        // which specifically exercise the MapToIPv4 normalisation path.

        [TestMethod]
        public async Task LoopbackIPv4_PassesThrough()
        {
            bool nextCalled = false;
            GeoLockMiddleware middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            await middleware.InvokeAsync(BuildContext("127.0.0.1"));

            Assert.IsTrue(nextCalled);
        }

        [TestMethod]
        public async Task LoopbackIPv6_PassesThrough()
        {
            bool nextCalled = false;
            GeoLockMiddleware middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            await middleware.InvokeAsync(BuildContext("::1"));

            Assert.IsTrue(nextCalled);
        }

        [TestMethod]
        public async Task PrivateIP_10Range_PassesThrough()
        {
            bool nextCalled = false;
            GeoLockMiddleware middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            await middleware.InvokeAsync(BuildContext("10.0.0.1"));

            Assert.IsTrue(nextCalled);
        }

        [TestMethod]
        public async Task PrivateIP_172Range_PassesThrough()
        {
            bool nextCalled = false;
            GeoLockMiddleware middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            // 172.16.0.1 – 172.31.255.255 are RFC 1918 private
            await middleware.InvokeAsync(BuildContext("172.16.0.1"));

            Assert.IsTrue(nextCalled);
        }

        [TestMethod]
        public async Task PrivateIP_172RangeBoundary_PassesThrough()
        {
            bool nextCalled = false;
            GeoLockMiddleware middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            await middleware.InvokeAsync(BuildContext("172.31.255.255"));

            Assert.IsTrue(nextCalled);
        }

        [TestMethod]
        public async Task PrivateIP_192Range_PassesThrough()
        {
            bool nextCalled = false;
            GeoLockMiddleware middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            await middleware.InvokeAsync(BuildContext("192.168.1.100"));

            Assert.IsTrue(nextCalled);
        }

        [TestMethod]
        public async Task LinkLocalIP_PassesThrough()
        {
            bool nextCalled = false;
            GeoLockMiddleware middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            await middleware.InvokeAsync(BuildContext("169.254.1.1"));

            Assert.IsTrue(nextCalled);
        }

        [TestMethod]
        public async Task NullRemoteIP_PassesThrough()
        {
            bool nextCalled = false;
            GeoLockMiddleware middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            await middleware.InvokeAsync(BuildContext(null));

            Assert.IsTrue(nextCalled);
        }

        // --- IPv4-mapped IPv6 normalisation ---
        // These verify that ::ffff:x.x.x.x addresses are correctly mapped to IPv4
        // before the loopback / private-range checks are applied.

        [TestMethod]
        public async Task IPv4MappedIPv6Loopback_PassesThrough()
        {
            bool nextCalled = false;
            GeoLockMiddleware middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            await middleware.InvokeAsync(BuildContext("::ffff:127.0.0.1"));

            Assert.IsTrue(nextCalled);
        }

        [TestMethod]
        public async Task IPv4MappedIPv6Private192_PassesThrough()
        {
            bool nextCalled = false;
            GeoLockMiddleware middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            await middleware.InvokeAsync(BuildContext("::ffff:192.168.1.1"));

            Assert.IsTrue(nextCalled);
        }

        [TestMethod]
        public async Task IPv4MappedIPv6Private10_PassesThrough()
        {
            bool nextCalled = false;
            GeoLockMiddleware middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            await middleware.InvokeAsync(BuildContext("::ffff:10.0.0.1"));

            Assert.IsTrue(nextCalled);
        }

        // --- AllowedIPs whitelist ---

        [TestMethod]
        public async Task AllowedIP_PassesThrough()
        {
            bool nextCalled = false;
            IConfiguration config = BuildConfig(allowedIPs: ["203.0.113.5"]);
            GeoLockMiddleware middleware = BuildMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, config);

            await middleware.InvokeAsync(BuildContext("203.0.113.5"));

            Assert.IsTrue(nextCalled);
        }

        [TestMethod]
        public async Task MultipleAllowedIPs_EachPassesThrough()
        {
            IConfiguration config = BuildConfig(allowedIPs: ["203.0.113.5", "203.0.113.6"]);
            GeoLockMiddleware middleware = BuildMiddleware(_ => Task.CompletedTask, config);

            bool first = false, second = false;
            var m1 = BuildMiddleware(_ => { first = true; return Task.CompletedTask; }, config);
            var m2 = BuildMiddleware(_ => { second = true; return Task.CompletedTask; }, config);

            await m1.InvokeAsync(BuildContext("203.0.113.5"));
            await m2.InvokeAsync(BuildContext("203.0.113.6"));

            Assert.IsTrue(first, "First allowed IP should pass through.");
            Assert.IsTrue(second, "Second allowed IP should pass through.");
        }
    }
}

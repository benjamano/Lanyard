using System.Security.Claims;
using Lanyard.Application.Services.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Authentication
{
    [TestClass]
    public class ClientRequestAuthorizationTests
    {
        private const string ConfiguredSecret = "correct-horse-battery-staple";

        private static ClientSecretValidator BuildValidator(string? configuredSecret, string environmentName)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Clients:SharedSecret"] = configuredSecret
                })
                .Build();

            Mock<IHostEnvironment> environment = new();
            environment.Setup(e => e.EnvironmentName).Returns(environmentName);

            return new ClientSecretValidator(configuration, NullLogger<ClientSecretValidator>.Instance, environment.Object);
        }

        private static HttpContext BuildHttpContext(bool authenticated = false, string? querySecret = null)
        {
            ServiceCollection services = new();
            services.AddLogging();
            ServiceProvider provider = services.BuildServiceProvider();

            DefaultHttpContext httpContext = new()
            {
                RequestServices = provider
            };

            if (authenticated)
            {
                httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "staffmember") }, "TestAuth"));
            }

            if (querySecret is not null)
            {
                httpContext.Request.QueryString = new QueryString($"?secret={querySecret}");
            }

            return httpContext;
        }

        [TestMethod]
        public void IsAuthorized_ValidSecretInQuery_ReturnsTrue()
        {
            ClientSecretValidator validator = BuildValidator(ConfiguredSecret, Environments.Production);
            HttpContext httpContext = BuildHttpContext(querySecret: ConfiguredSecret);

            bool result = ClientRequestAuthorization.IsAuthorized(httpContext, validator);

            Assert.IsTrue(result);
        }

        [TestMethod]
        public void IsAuthorized_InvalidSecretInQuery_ReturnsFalse()
        {
            ClientSecretValidator validator = BuildValidator(ConfiguredSecret, Environments.Production);
            HttpContext httpContext = BuildHttpContext(querySecret: "wrong-secret");

            bool result = ClientRequestAuthorization.IsAuthorized(httpContext, validator);

            Assert.IsFalse(result);
        }

        [TestMethod]
        public void IsAuthorized_AuthenticatedUser_BypassesSecretCheck()
        {
            ClientSecretValidator validator = BuildValidator(ConfiguredSecret, Environments.Production);
            HttpContext httpContext = BuildHttpContext(authenticated: true);

            bool result = ClientRequestAuthorization.IsAuthorized(httpContext, validator);

            Assert.IsTrue(result);
        }

        [TestMethod]
        public void IsAuthorized_UnsetSecret_OutsideDevelopment_ReturnsFalse()
        {
            ClientSecretValidator validator = BuildValidator(configuredSecret: null, Environments.Production);
            HttpContext httpContext = BuildHttpContext();

            bool result = ClientRequestAuthorization.IsAuthorized(httpContext, validator);

            Assert.IsFalse(result);
        }

        [TestMethod]
        public void IsAuthorized_UnsetSecret_InDevelopment_ReturnsTrue()
        {
            ClientSecretValidator validator = BuildValidator(configuredSecret: null, Environments.Development);
            HttpContext httpContext = BuildHttpContext();

            bool result = ClientRequestAuthorization.IsAuthorized(httpContext, validator);

            Assert.IsTrue(result);
        }

        [TestMethod]
        public void EvaluateAndLog_Denied_LogsWarningWithRemoteIp()
        {
            ClientSecretValidator validator = BuildValidator(ConfiguredSecret, Environments.Production);
            Mock<ILogger> logger = new();
            logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            bool result = ClientRequestAuthorization.EvaluateAndLog(validator, "wrong-secret", logger.Object, "203.0.113.5", "/websocket");

            Assert.IsFalse(result);
            logger.Verify(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("203.0.113.5")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
    }
}

using Lanyard.Application.Services.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Authentication
{
    [TestClass]
    public class ClientSecretValidatorTests
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

        [TestMethod]
        public void Authorize_ConfiguredSecret_ValidSecretProvided_ReturnsAllowed()
        {
            ClientSecretValidator validator = BuildValidator(ConfiguredSecret, Environments.Production);

            ClientSecretAuthorizationOutcome outcome = validator.Authorize(ConfiguredSecret);

            Assert.AreEqual(ClientSecretAuthorizationOutcome.Allowed, outcome);
        }

        [TestMethod]
        public void Authorize_ConfiguredSecret_InvalidSecretProvided_ReturnsDenied()
        {
            ClientSecretValidator validator = BuildValidator(ConfiguredSecret, Environments.Production);

            ClientSecretAuthorizationOutcome outcome = validator.Authorize("wrong-secret");

            Assert.AreEqual(ClientSecretAuthorizationOutcome.Denied, outcome);
        }

        [TestMethod]
        public void Authorize_UnconfiguredSecret_InProduction_ReturnsDenied()
        {
            ClientSecretValidator validator = BuildValidator(configuredSecret: null, Environments.Production);

            ClientSecretAuthorizationOutcome outcome = validator.Authorize(provided: null);

            Assert.AreEqual(ClientSecretAuthorizationOutcome.Denied, outcome);
        }

        [TestMethod]
        public void Authorize_UnconfiguredSecret_InDevelopment_ReturnsAllowedUnconfiguredDevelopment()
        {
            ClientSecretValidator validator = BuildValidator(configuredSecret: null, Environments.Development);

            ClientSecretAuthorizationOutcome outcome = validator.Authorize(provided: null);

            Assert.AreEqual(ClientSecretAuthorizationOutcome.AllowedUnconfiguredDevelopment, outcome);
        }
    }
}

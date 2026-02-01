using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Application.Services;
using Lanyard.Application.SignalR;
using Microsoft.AspNetCore.SignalR;

namespace Lanyard.Tests.Services.Clients
{
    [TestClass]
    public class ClientServiceTests
    {
        public TestContext TestContext { get; set; }

        private DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
        {
            return new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
        }

        private ClientService GetService(DbContextOptions<ApplicationDbContext> options)
        {
            var factoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
            factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(() => new ApplicationDbContext(options));

            var signalRProjectionControlHubMock = new Mock<ISignalRProjectionControlHub>();
            var serviceProviderMock = new Mock<IServiceProvider>();
            serviceProviderMock.Setup(sp => sp.GetService(typeof(ISignalRProjectionControlHub)))
                .Returns(signalRProjectionControlHubMock.Object);

            var hubContextMock = new Mock<IHubContext<SignalRControlHub>>();

            return new ClientService(factoryMock.Object, serviceProviderMock.Object, hubContextMock.Object);
        }

        private ProjectionProgramService GetProjectionProgramService(DbContextOptions<ApplicationDbContext> options)
        {
            var factoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
            factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(() => new ApplicationDbContext(options));

            var clientServiceMock = new Mock<IClientService>();

            return new ProjectionProgramService(factoryMock.Object, clientServiceMock.Object);
        }

        [TestMethod]
        public async Task CreateClientAsync_ShouldAddClient()
        {
            var options = GetInMemoryOptions();

            var ctx = new ApplicationDbContext(options);

            var service = GetService(options);

            var client = new Client { Id = Guid.NewGuid(), Name = "Test Client" };

            var result = await service.CreateClientAsync(client);

            Assert.IsTrue(result.Success);
            Assert.IsNotNull(await ctx.Clients.FindAsync(client.Id));
        }

        [TestMethod]
        public async Task GetClientFromIdAsync_ShouldReturnClient()
        {
            var options = GetInMemoryOptions();

            var ctx = new ApplicationDbContext(options);

            var client = new Client { Id = Guid.NewGuid(), Name = "Test Client" };

            ctx.Clients.Add(client);

            ctx.SaveChanges();

            var service = GetService(options);

            var result = await service.GetClientFromIdAsync(client.Id);

            Assert.IsTrue(result.Success);

            Assert.AreEqual(client.Id, result.Data?.Id);
        }

        [TestMethod]
        public async Task UpdateClientAsync_ShouldUpdateClient()
        {
            var options = GetInMemoryOptions();

            var ctx = new ApplicationDbContext(options);

            var client = new Client { Id = Guid.NewGuid(), Name = "Old Name" };

            ctx.Clients.Add(client);

            ctx.SaveChanges();

            var service = GetService(options);

            client.Name = "New Name";

            var result = await service.UpdateClientAsync(client);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("New Name", (await ctx.Clients.FindAsync(client.Id))?.Name);
        }

        [TestMethod]
        public async Task GetConnectedClientsAsync_ShouldReturnClientsWithoutError()
        {
            var options = GetInMemoryOptions();

            var service = GetService(options);

            var result = await service.GetConnectedClientsAsync();

            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Data);
        }

        [TestMethod]
        public async Task GetClientsAsync_ShouldReturnAddedClients()
        {
            var options = GetInMemoryOptions();

            var service = GetService(options);

            var result = await service.CreateClientAsync(new Client { Id = Guid.NewGuid(), Name = "Client 1", MostRecentConnectionId = Guid.NewGuid().ToString() });

            Assert.IsTrue(result.Success);

            var result1 = await service.GetClientsAsync();

            TestContext.WriteLine("Connected Clients Count: " + result1.Data?.Count());

            Assert.IsTrue(result1.Success);
            Assert.IsNotNull(result1.Data);
            Assert.AreEqual(1, result1.Data?.Count(), "Expected one connected client.");
        }

        [TestMethod]
        public async Task GetClientsAsync_ShouldReturnEmptyListWhenNoClients()
        {
            var options = GetInMemoryOptions();

            var service = GetService(options);

            var result = await service.GetClientsAsync();

            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Data);
            Assert.AreEqual(0, result.Data?.Count(), "Expected zero connected clients.");
        }

        [TestMethod]
        public async Task GetClientsWithCapabilitiesAsync_ShouldReturnEmptyListWhenNoClients()
        {
            var options = GetInMemoryOptions();

            var service = GetService(options);

            var result = await service.GetClientsWithCapabilitiesAsync();

            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Data);
            Assert.AreEqual(0, result.Data?.Count(), "Expected zero connected clients with capabilities.");
        }

        [TestMethod]
        public async Task GetClientsWithCapabilitiesAsync_ShouldReturnClientWithCapabilities()
        {
            var options = GetInMemoryOptions();

            var service = GetService(options);

            var resultCreate = await service.CreateClientAsync(new Client
            {
                Id = Guid.NewGuid(),
                Name = "Client 1",
                MostRecentConnectionId = Guid.NewGuid().ToString(),
            });

            Assert.IsTrue(resultCreate.Success);
            Assert.IsNotNull(resultCreate.Data);

            var resultCreate1 = await service.AddClientProjectionAsync(new ClientProjectionSettings
            {
                ClientId = resultCreate.Data.Id,
                IsActive = true,
                ProjectionProgramId = Guid.NewGuid(),
                DisplayIndex = 0,
                Height = 1080,
                Width = 1920,
            });

            Assert.IsTrue(resultCreate1.Success, resultCreate1.Error);

            var result = await service.GetClientsWithCapabilitiesAsync();

            TestContext.WriteLine("Clients with Capabilities Count: " + result.Data?.Count());

            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Data);
            Assert.AreEqual(1, result.Data?.Count(), "Expected one connected client with capabilities.");
        }

        [TestMethod]
        public async Task GetClientProjectionSettingsAsync_ShouldReturnEmptyListWithInvalidClient()
        {
            var options = GetInMemoryOptions();

            var service = GetService(options);

            var result = await service.GetClientProjectionSettingsAsync(Guid.NewGuid());

            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Data);
            Assert.AreEqual(0, result.Data?.Count(), "Expected zero client projection settings.");
        }

        [TestMethod]
        public async Task AddClientProjectionAsync_ShouldNotAddProjectionForInvalidClient()
        {
            var options = GetInMemoryOptions();

            var service = GetService(options);

            var result = await service.AddClientProjectionAsync(new ClientProjectionSettings
            {
                ClientId = Guid.Empty,
                ProjectionProgramId = Guid.NewGuid(),
            });

            Assert.IsFalse(result.Success);
            Assert.AreEqual("Client ID is required.", result.Error);
        }

        [TestMethod]
        public async Task AddClientProjectionAsync_ShouldNotAddProjectionForInvalidProjectionProgram()
        {
            var options = GetInMemoryOptions();

            var service = GetService(options);

            var result = await service.AddClientProjectionAsync(new ClientProjectionSettings
            {
                ProjectionProgramId = Guid.Empty,
            });

            Assert.IsFalse(result.Success);
            Assert.AreEqual("Projection program ID is required.", result.Error);
        }

        [TestMethod]
        public async Task AddClientProjectionAsync_ShouldNotAddProjectionForInvalidDisplayIndex()
        {
            var options = GetInMemoryOptions();

            var service = GetService(options);

            var result = await service.AddClientProjectionAsync(new ClientProjectionSettings
            {
                ProjectionProgramId = Guid.NewGuid(),
                ClientId = Guid.NewGuid(),
                DisplayIndex = -1,
            });

            Assert.IsFalse(result.Success);
            Assert.AreEqual("Display index must be zero or greater.", result.Error);
        }

        [TestMethod]
        public async Task AddClientProjectionAsync_ShouldNotAddProjectionForInvalidWidthOrHeight()
        {
            var options = GetInMemoryOptions();

            var service = GetService(options);

            var result = await service.AddClientProjectionAsync(new ClientProjectionSettings
            {
                ProjectionProgramId = Guid.NewGuid(),
                ClientId = Guid.NewGuid(),
                DisplayIndex = 0,
                Height = -1,
            });

            Assert.IsFalse(result.Success);
            Assert.AreEqual("Height must be greater than zero.", result.Error);

            var result1 = await service.AddClientProjectionAsync(new ClientProjectionSettings
            {
                ProjectionProgramId = Guid.NewGuid(),
                ClientId = Guid.NewGuid(),
                DisplayIndex = 0,
                Height = 0,
            });

            Assert.IsFalse(result1.Success);
            Assert.AreEqual("Height must be greater than zero.", result.Error);

            var result2 = await service.AddClientProjectionAsync(new ClientProjectionSettings
            {
                ProjectionProgramId = Guid.NewGuid(),
                ClientId = Guid.NewGuid(),
                DisplayIndex = 0,
                Height = 1080,
                Width = -1
            });

            Assert.IsFalse(result2.Success);
            Assert.AreEqual("Width must be greater than zero.", result2.Error);

            var result3 = await service.AddClientProjectionAsync(new ClientProjectionSettings
            {
                ProjectionProgramId = Guid.NewGuid(),
                ClientId = Guid.NewGuid(),
                DisplayIndex = 0,
                Height = 1080,
                Width = 0
            });

            Assert.IsFalse(result3.Success);
            Assert.AreEqual("Width must be greater than zero.", result3.Error);
        }

        [TestMethod]
        public async Task GetClientProjectionSettingsAsync_ShouldReturnClientProjectionSettings()
        {
            var options = GetInMemoryOptions();

            var service = GetService(options);

            var projectionService = GetProjectionProgramService(options);

            var resultCreate2 = await projectionService.CreateProjectionProgramAsync(new ProjectionProgram
            {
                Name = "Projection Program",
                IsActive = true
            });

            Assert.IsTrue(resultCreate2.Success);
            Assert.IsNotNull(resultCreate2.Data);

            var resultCreate = await service.CreateClientAsync(new Client
            {
                Name = "Client 1",
                MostRecentConnectionId = Guid.NewGuid().ToString(),
            });

            Assert.IsTrue(resultCreate.Success, resultCreate.Error);
            Assert.IsNotNull(resultCreate.Data);

            var resultCreate1 = await service.AddClientProjectionAsync(new ClientProjectionSettings
            {
                ClientId = resultCreate.Data.Id,
                IsActive = true,
                ProjectionProgramId = resultCreate2.Data.Id,
                DisplayIndex = 0,
                Height = 1080,
                Width = 1920,
            });
            Assert.IsTrue(resultCreate1.Success, resultCreate1.Error);

            var result = await service.GetClientProjectionSettingsAsync(resultCreate.Data.Id);

            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Data);
            Assert.AreEqual(1, result.Data?.Count(), "Expected one client projection setting.");
        }

        [TestMethod]
        public async Task SetClientAvailableScreensAsync_ShouldUpdateScreens()
        {
            var options = GetInMemoryOptions();

            var ctx = new ApplicationDbContext(options);

            var service = GetService(options);

            var resultCreate = await service.CreateClientAsync(new Client
            {
                Name = "Client 1",
                MostRecentConnectionId = Guid.NewGuid().ToString(),
            });

            Assert.IsTrue(resultCreate.Success);
            Assert.IsNotNull(resultCreate.Data);

            var screens = new List<ClientAvailableScreenDTO>
            {
                new() { Name = "Screen1", Width = 1920, Height = 1080 },
                new() { Name = "Screen2", Width = 1280, Height = 720 }
            };

            var result = await service.SetClientAvailableScreensAsync(resultCreate.Data.Id, screens);

            Assert.IsTrue(result.Success);

            var updatedClientResult = await service.GetClientAvailableScreensAsync(resultCreate.Data.Id);

            Assert.IsTrue(updatedClientResult.Success);
            Assert.IsNotNull(updatedClientResult.Data);
            Assert.AreEqual(2, updatedClientResult.Data!.Count(), "Expected two available screens.");
        }

        [TestMethod]
        public async Task SetClientAvailableScreensAsync_ReturnsFailureWhenClientNotFound()
        {
            var options = GetInMemoryOptions();

            var ctx = new ApplicationDbContext(options);

            var service = GetService(options);

            var screens = new List<ClientAvailableScreenDTO>
            {
                new() { Name = "Screen1", Width = 1920, Height = 1080 },
                new() { Name = "Screen2", Width = 1280, Height = 720 }
            };

            var result = await service.SetClientAvailableScreensAsync(Guid.NewGuid(), screens);

            Assert.IsFalse(result.Success);
            Assert.AreEqual("Client not found for the given client ID.", result.Error);
        }

        [TestMethod]
        public async Task GetClientAvailableScreensAsync_ReturnsEmptyListWhenClientNotFound()
        {
            var options = GetInMemoryOptions();

            var ctx = new ApplicationDbContext(options);

            var service = GetService(options);

            var result = await service.GetClientAvailableScreensAsync(Guid.NewGuid());

            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Data);
            Assert.AreEqual(0, result.Data!.Count(), "Expected no available screens.");
        }

        [TestMethod]
        public async Task DeleteClientProjectionSettingsAsync_ReturnsFailureWhenProjectionSettingsNotFound()
        {
            var options = GetInMemoryOptions();

            var ctx = new ApplicationDbContext(options);

            var service = GetService(options);

            var result = await service.DeleteClientProjectionSettingsAsync(Guid.NewGuid());

            Assert.IsFalse(result.Success);
            Assert.AreEqual("Client projection settings not found.", result.Error);
        }

        [TestMethod]
        public async Task DeleteClientProjectionSettingsAsync_DeletesClientProjectionSettings()
        {
            var options = GetInMemoryOptions();

            var ctx = new ApplicationDbContext(options);

            var service = GetService(options);

            var resultCreate = await service.CreateClientAsync(new Client
            {
                Id = Guid.NewGuid(),
                Name = "Client 1",
                MostRecentConnectionId = Guid.NewGuid().ToString(),
            });

            Assert.IsTrue(resultCreate.Success);
            Assert.IsNotNull(resultCreate.Data);

            var resultCreate1 = await service.AddClientProjectionAsync(new ClientProjectionSettings
            {
                ClientId = resultCreate.Data.Id,
                IsActive = true,
                ProjectionProgramId = Guid.NewGuid(),
                DisplayIndex = 0,
                Height = 1080,
                Width = 1920,
            });

            Assert.IsTrue(resultCreate1.Success, resultCreate1.Error);

            var result = await service.DeleteClientProjectionSettingsAsync(resultCreate1.Data);

            Assert.IsTrue(result.Success);

            var projectionSettings = await service.GetClientProjectionSettingsAsync(resultCreate.Data.Id);

            Assert.IsTrue(projectionSettings.Success);

            Assert.AreEqual(0, projectionSettings.Data?.Count(), "Expected zero client projection settings after deletion.");
        }

        [TestMethod]
        public async Task GetProjectionProgramAsync_ShouldReturnErrorWhenNotFound()
        {
            var options = GetInMemoryOptions();

            var service = GetProjectionProgramService(options);

            var result = await service.GetProjectionProgramAsync(Guid.NewGuid());

            Assert.IsFalse(result.Success);
            Assert.AreEqual("Projection program not found.", result.Error);
        }

        [TestMethod]
        public async Task GetProjectionProgramAsync_ShouldReturnProjectionProgram()
        {
            var options = GetInMemoryOptions();

            var service = GetProjectionProgramService(options);

            var result1 = await service.CreateProjectionProgramAsync(new ProjectionProgram
            {
                Name = "Test Program",
                IsActive = true
            });

            Assert.IsTrue(result1.IsSuccess);
            Assert.IsNotNull(result1.Data);

            var result = await service.GetProjectionProgramAsync(result1.Data.Id);

            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Data);
            Assert.AreEqual("Test Program", result.Data.Name);
        }
    }
}

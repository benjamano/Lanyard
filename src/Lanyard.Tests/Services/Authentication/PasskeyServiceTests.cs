using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Demo;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Integration;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Authentication
{
    [TestClass]
    public class PasskeyServiceTests
    {
        private const string UserId = "user-1";
        private const string OtherUserId = "user-2";

        private UserManager<UserProfile> _userManager = null!;
        private ServiceProvider _provider = null!;

        [TestInitialize]
        public async Task Setup()
        {
            ServiceCollection services = new();
            string databaseName = Guid.NewGuid().ToString();

            services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(databaseName));
            services.AddDataProtection();
            services.AddLogging();
            services.AddIdentityCore<UserProfile>()
                .AddRoles<ApplicationRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>();

            // EF InMemory can't store Identity's passkey rows - see InMemoryPasskeyUserStore.
            services.AddSingleton<InMemoryPasskeyUserStore.PasskeyTable>();
            services.AddScoped<IUserStore<UserProfile>, InMemoryPasskeyUserStore>();

            _provider = services.BuildServiceProvider();
            _userManager = _provider.CreateScope().ServiceProvider.GetRequiredService<UserManager<UserProfile>>();

            await _userManager.CreateAsync(new UserProfile { Id = UserId, UserName = "alice" });
            await _userManager.CreateAsync(new UserProfile { Id = OtherUserId, UserName = "bob" });
        }

        [TestCleanup]
        public void Cleanup()
        {
            _provider.Dispose();
        }

        private PasskeyService BuildService(string currentUserId = UserId, bool isDemo = false)
        {
            Mock<ICurrentUserAccessor> currentUser = new();
            currentUser.Setup(x => x.GetCurrentUserIdAsync()).ReturnsAsync(Result<string>.Ok(currentUserId));

            Mock<IDemoGuard> demoGuard = new();
            demoGuard.Setup(x => x.IsDemoSessionAsync()).ReturnsAsync(isDemo);

            return new PasskeyService(_userManager, currentUser.Object, NullLogger<PasskeyService>.Instance, demoGuard.Object);
        }

        private static UserPasskeyInfo NewPasskey(DateTimeOffset? createdAt = null, bool isBackedUp = true) => new(
            RandomNumberGenerator.GetBytes(32),
            publicKey: [1, 2, 3],
            createdAt ?? DateTimeOffset.UtcNow,
            signCount: 0,
            transports: ["internal"],
            isUserVerified: true,
            isBackupEligible: true,
            isBackedUp: isBackedUp,
            attestationObject: [4],
            clientDataJson: [5]);

        private async Task<UserPasskeyInfo> AddPasskeyDirectlyAsync(string userId, string name = "iPhone", DateTimeOffset? createdAt = null)
        {
            UserPasskeyInfo passkey = NewPasskey(createdAt);
            passkey.Name = name;
            await _userManager.AddOrUpdatePasskeyAsync((await _userManager.FindByIdAsync(userId))!, passkey);
            return passkey;
        }

        [TestMethod]
        public async Task SavePasskeyAsync_NamesItAfterTheDevice()
        {
            PasskeyService service = BuildService();

            Result<string> result = await service.SavePasskeyAsync(UserId, NewPasskey(),
                "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Mobile Safari/537.36");

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.AreEqual("Android device", result.Data);

            IList<UserPasskeyInfo> stored = await _userManager.GetPasskeysAsync((await _userManager.FindByIdAsync(UserId))!);
            Assert.AreEqual(1, stored.Count);
            Assert.AreEqual("Android device", stored[0].Name);
        }

        [TestMethod]
        public async Task SavePasskeyAsync_AtTheLimit_Fails()
        {
            for (int i = 0; i < PasskeyService.MaxPasskeysPerUser; i++)
            {
                await AddPasskeyDirectlyAsync(UserId);
            }

            PasskeyService service = BuildService();

            Result<string> result = await service.SavePasskeyAsync(UserId, NewPasskey(), null);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(PasskeyService.LimitReachedMessage, result.Error);
        }

        [TestMethod]
        public async Task CanAddPasskeyAsync_InTheDemo_Fails()
        {
            PasskeyService service = BuildService(isDemo: true);

            Result<bool> result = await service.CanAddPasskeyAsync(UserId);

            Assert.IsFalse(result.IsSuccess, "Demo accounts are shared, so nobody may add a passkey to one.");
            Assert.AreEqual(DemoGuard.NotInDemoMessage, result.Error);
        }

        [TestMethod]
        public async Task CanAddPasskeyAsync_UnknownUser_Fails()
        {
            PasskeyService service = BuildService();

            Result<bool> result = await service.CanAddPasskeyAsync("no-such-user");

            Assert.IsFalse(result.IsSuccess);
        }

        [TestMethod]
        public async Task GetMyPasskeysAsync_ReturnsOnlyTheCurrentUsersPasskeys_OldestFirst()
        {
            await AddPasskeyDirectlyAsync(UserId, "Mac", DateTimeOffset.UtcNow.AddDays(-1));
            await AddPasskeyDirectlyAsync(UserId, "iPhone", DateTimeOffset.UtcNow.AddDays(-5));
            await AddPasskeyDirectlyAsync(OtherUserId, "Bob's phone");

            PasskeyService service = BuildService();

            Result<List<PasskeySummaryDto>> result = await service.GetMyPasskeysAsync();

            Assert.IsTrue(result.IsSuccess, result.Error);
            CollectionAssert.AreEqual(new[] { "iPhone", "Mac" }, result.Data!.Select(x => x.Name).ToArray());
            Assert.IsTrue(result.Data!.All(x => x.IsSynced));
        }

        [TestMethod]
        public async Task RemoveMyPasskeyAsync_OwnPasskey_RemovesIt()
        {
            UserPasskeyInfo passkey = await AddPasskeyDirectlyAsync(UserId);
            PasskeyService service = BuildService();

            Result<bool> result = await service.RemoveMyPasskeyAsync(Base64Url.EncodeToString(passkey.CredentialId));

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.AreEqual(0, (await _userManager.GetPasskeysAsync((await _userManager.FindByIdAsync(UserId))!)).Count);
        }

        [TestMethod]
        public async Task RemoveMyPasskeyAsync_SomeoneElsesPasskey_LeavesItAlone()
        {
            UserPasskeyInfo bobsPasskey = await AddPasskeyDirectlyAsync(OtherUserId);
            PasskeyService service = BuildService(currentUserId: UserId);

            Result<bool> result = await service.RemoveMyPasskeyAsync(Base64Url.EncodeToString(bobsPasskey.CredentialId));

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(1, (await _userManager.GetPasskeysAsync((await _userManager.FindByIdAsync(OtherUserId))!)).Count,
                "Knowing another account's credential id must not let you remove it.");
        }

        [TestMethod]
        public async Task RemoveMyPasskeyAsync_MalformedId_Fails()
        {
            PasskeyService service = BuildService();

            Result<bool> result = await service.RemoveMyPasskeyAsync("not base64url!!");

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("Passkey not found", result.Error);
        }

        [TestMethod]
        public async Task RemoveMyPasskeyAsync_InTheDemo_Fails()
        {
            UserPasskeyInfo passkey = await AddPasskeyDirectlyAsync(UserId);
            PasskeyService service = BuildService(isDemo: true);

            Result<bool> result = await service.RemoveMyPasskeyAsync(Base64Url.EncodeToString(passkey.CredentialId));

            Assert.IsFalse(result.IsSuccess);
        }

        [TestMethod]
        [DataRow("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15", "iPhone")]
        [DataRow("Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605.1.15", "iPad")]
        [DataRow("Mozilla/5.0 (Linux; Android 14; SM-S911B) AppleWebKit/537.36", "Android device")]
        [DataRow("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15", "Mac")]
        [DataRow("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36", "Windows PC")]
        [DataRow("Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36", "Chromebook")]
        [DataRow("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36", "Linux PC")]
        [DataRow("curl/8.0", "Passkey")]
        [DataRow(null, "Passkey")]
        public void NameForDevice_RecognisesCommonDevices(string? userAgent, string expected)
        {
            Assert.AreEqual(expected, PasskeyService.NameForDevice(userAgent));
        }
    }
}

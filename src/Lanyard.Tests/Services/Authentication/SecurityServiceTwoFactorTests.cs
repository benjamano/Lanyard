using System;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Training;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Authentication
{
    [TestClass]
    public class SecurityServiceTwoFactorTests
    {
        private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
        {
            return new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
        }

        // See SecurityServiceTests.BuildUserManager: a real Identity DI container is required
        // because AddDefaultTokenProviders wires up DataProtectionTokenProvider<TUser>, which is
        // internal and can't be constructed directly. This also gives GenerateTwoFactorTokenAsync/
        // VerifyTwoFactorTokenAsync a genuinely working AuthenticatorTokenProvider to exercise.
        private static UserManager<UserProfile> BuildUserManager(DbContextOptions<ApplicationDbContext> options)
        {
            ServiceCollection services = new();

            services.AddSingleton(options);
            services.AddScoped(sp => new ApplicationDbContext(sp.GetRequiredService<DbContextOptions<ApplicationDbContext>>()));
            services.AddDataProtection();
            services.AddLogging();

            services.AddIdentityCore<UserProfile>(o => o.Password.RequireNonAlphanumeric = false)
                .AddRoles<ApplicationRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();

            ServiceProvider provider = services.BuildServiceProvider();
            return provider.GetRequiredService<UserManager<UserProfile>>();
        }

        private static Mock<AuthenticationStateProvider> BuildAuthProviderForUser(string userId)
        {
            ClaimsIdentity identity = new(
                [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Name, "testuser")],
                "TestAuth");

            AuthenticationState state = new(new ClaimsPrincipal(identity));

            Mock<AuthenticationStateProvider> mock = new();
            mock.Setup(p => p.GetAuthenticationStateAsync()).ReturnsAsync(state);
            return mock;
        }

        private class TestNavigationManager : NavigationManager
        {
            public TestNavigationManager()
            {
                Initialize("https://test.lanyard.local/", "https://test.lanyard.local/");
            }
        }

        private static SecurityService BuildService(
            DbContextOptions<ApplicationDbContext> options,
            UserManager<UserProfile> userManager,
            string userId)
        {
            Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
            factoryMock.Setup(f => f.CreateDbContext()).Returns(() => new ApplicationDbContext(options));

            Mock<ICourseService> courseServiceMock = new();
            Mock<ICourseAssignmentService> courseAssignmentServiceMock = new();
            Mock<ICompanyLocationService> companyLocationServiceMock = new();
            Mock<IEmailService> emailServiceMock = new();

            return new SecurityService(
                BuildAuthProviderForUser(userId).Object,
                factoryMock.Object,
                userManager,
                courseAssignmentServiceMock.Object,
                courseServiceMock.Object,
                NullLogger<SecurityService>.Instance,
                new TestNavigationManager(),
                emailServiceMock.Object,
                companyLocationServiceMock.Object,
                Options.Create(new EmailOptions()));
        }

        private static async Task<UserProfile> CreateUserAsync(UserManager<UserProfile> userManager, string password = "P@ssword1")
        {
            UserProfile user = new()
            {
                UserName = "jdoe",
                Email = "jane@example.com"
            };

            IdentityResult result = await userManager.CreateAsync(user, password);
            Assert.IsTrue(result.Succeeded, string.Join(", ", result.Errors));

            return user;
        }

        // Re-derives the exact TOTP value AuthenticatorTokenProvider<TUser> expects, so the
        // "right code" path can be exercised for real instead of mocking Identity's internal
        // Rfc6238AuthenticationService (which is inaccessible from outside the assembly).
        private static string GenerateTotpCode(string base32Key)
        {
            byte[] key = Base32Decode(base32Key);
            long unixTimeStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

            byte[] timestepBytes = BitConverter.GetBytes(unixTimeStep);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(timestepBytes);
            }

            using HMACSHA1 hmac = new(key);
            byte[] hash = hmac.ComputeHash(timestepBytes);

            int offset = hash[^1] & 0xf;
            int binaryCode = ((hash[offset] & 0x7f) << 24)
                | ((hash[offset + 1] & 0xff) << 16)
                | ((hash[offset + 2] & 0xff) << 8)
                | (hash[offset + 3] & 0xff);

            return (binaryCode % 1_000_000).ToString("D6");
        }

        private static byte[] Base32Decode(string input)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            input = input.Replace(" ", "").TrimEnd('=').ToUpperInvariant();

            byte[] result = new byte[input.Length * 5 / 8];
            int bitBuffer = 0;
            int bitCount = 0;
            int index = 0;

            foreach (char c in input)
            {
                int value = alphabet.IndexOf(c);
                if (value < 0)
                {
                    continue;
                }

                bitBuffer = (bitBuffer << 5) | value;
                bitCount += 5;

                if (bitCount >= 8)
                {
                    result[index++] = (byte)(bitBuffer >> (bitCount - 8));
                    bitCount -= 8;
                }
            }

            return result;
        }

        [TestMethod]
        public async Task BeginAuthenticatorEnrollmentAsync_ReturnsKeyAndQrCode()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);
            UserProfile user = await CreateUserAsync(userManager);
            SecurityService service = BuildService(options, userManager, user.Id);

            Result<AuthenticatorEnrollmentDto> result = await service.BeginAuthenticatorEnrollmentAsync();

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Data!.SharedKey));
            Assert.StartsWith("otpauth://totp/", result.Data.AuthenticatorUri);
            Assert.StartsWith("data:image/png;base64,", result.Data.QrCodeDataUri);
        }

        [TestMethod]
        public async Task ConfirmAuthenticatorEnrollmentAsync_WrongCode_Fails()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);
            UserProfile user = await CreateUserAsync(userManager);
            SecurityService service = BuildService(options, userManager, user.Id);

            await service.BeginAuthenticatorEnrollmentAsync();
            Result<List<string>> result = await service.ConfirmAuthenticatorEnrollmentAsync("000000");

            Assert.IsFalse(result.IsSuccess);
            Assert.IsFalse(await userManager.GetTwoFactorEnabledAsync(user));
        }

        [TestMethod]
        public async Task ConfirmAuthenticatorEnrollmentAsync_RightCode_EnablesAndReturnsRecoveryCodes()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);
            UserProfile user = await CreateUserAsync(userManager);
            SecurityService service = BuildService(options, userManager, user.Id);

            Result<AuthenticatorEnrollmentDto> enrollment = await service.BeginAuthenticatorEnrollmentAsync();
            string code = GenerateTotpCode(enrollment.Data!.SharedKey);

            Result<List<string>> result = await service.ConfirmAuthenticatorEnrollmentAsync(code);

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.HasCount(8, result.Data!);
            Assert.IsTrue(await userManager.GetTwoFactorEnabledAsync(user));
        }

        [TestMethod]
        public async Task EnableEmailTwoFactorAsync_NoEmail_Fails()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);
            UserProfile user = new() { UserName = "noemail" };
            IdentityResult createResult = await userManager.CreateAsync(user, "P@ssword1");
            Assert.IsTrue(createResult.Succeeded, string.Join(", ", createResult.Errors));

            SecurityService service = BuildService(options, userManager, user.Id);

            Result<List<string>> result = await service.EnableEmailTwoFactorAsync();

            Assert.IsFalse(result.IsSuccess);
            Assert.IsFalse(await userManager.GetTwoFactorEnabledAsync(user));
        }

        [TestMethod]
        public async Task EnableEmailTwoFactorAsync_WithEmail_EnablesAndReturnsRecoveryCodes()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);
            UserProfile user = await CreateUserAsync(userManager);
            SecurityService service = BuildService(options, userManager, user.Id);

            Result<List<string>> result = await service.EnableEmailTwoFactorAsync();

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.HasCount(8, result.Data!);
            Assert.IsTrue(await userManager.GetTwoFactorEnabledAsync(user));
        }

        [TestMethod]
        public async Task GetTwoFactorStatusAsync_ReflectsAuthenticatorEnrollment()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);
            UserProfile user = await CreateUserAsync(userManager);
            SecurityService service = BuildService(options, userManager, user.Id);

            Result<AuthenticatorEnrollmentDto> enrollment = await service.BeginAuthenticatorEnrollmentAsync();
            await service.ConfirmAuthenticatorEnrollmentAsync(GenerateTotpCode(enrollment.Data!.SharedKey));

            Result<TwoFactorStatusDto> status = await service.GetTwoFactorStatusAsync();

            Assert.IsTrue(status.IsSuccess, status.Error);
            Assert.IsTrue(status.Data!.IsEnabled);
            Assert.IsTrue(status.Data.HasAuthenticator);
            Assert.IsFalse(status.Data.HasEmail);
            Assert.AreEqual(8, status.Data.RecoveryCodesRemaining);
        }

        [TestMethod]
        public async Task DisableTwoFactorAsync_WrongPassword_Fails()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);
            UserProfile user = await CreateUserAsync(userManager);
            SecurityService service = BuildService(options, userManager, user.Id);

            await service.EnableEmailTwoFactorAsync();
            Result<bool> result = await service.DisableTwoFactorAsync("wrong-password");

            Assert.IsFalse(result.IsSuccess);
            Assert.IsTrue(await userManager.GetTwoFactorEnabledAsync(user));
        }

        [TestMethod]
        public async Task DisableTwoFactorAsync_CorrectPassword_Disables()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);
            UserProfile user = await CreateUserAsync(userManager);
            SecurityService service = BuildService(options, userManager, user.Id);

            await service.EnableEmailTwoFactorAsync();
            Result<bool> result = await service.DisableTwoFactorAsync("P@ssword1");

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.IsFalse(await userManager.GetTwoFactorEnabledAsync(user));
        }

        [TestMethod]
        public async Task RegenerateRecoveryCodesAsync_NotEnabled_Fails()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);
            UserProfile user = await CreateUserAsync(userManager);
            SecurityService service = BuildService(options, userManager, user.Id);

            Result<List<string>> result = await service.RegenerateRecoveryCodesAsync();

            Assert.IsFalse(result.IsSuccess);
        }

        [TestMethod]
        public async Task RegenerateRecoveryCodesAsync_Enabled_ReturnsNewCodes()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);
            UserProfile user = await CreateUserAsync(userManager);
            SecurityService service = BuildService(options, userManager, user.Id);

            Result<List<string>> firstBatch = await service.EnableEmailTwoFactorAsync();
            Result<List<string>> secondBatch = await service.RegenerateRecoveryCodesAsync();

            Assert.IsTrue(secondBatch.IsSuccess, secondBatch.Error);
            Assert.HasCount(8, secondBatch.Data!);
            CollectionAssert.AreNotEquivalent(firstBatch.Data, secondBatch.Data);
        }
    }
}

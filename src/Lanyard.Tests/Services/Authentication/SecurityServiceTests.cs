using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Training;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Training;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Authentication
{
    [TestClass]
    public class SecurityServiceTests
    {
        private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
        {
            return new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
        }

        // DataProtectionTokenProvider<TUser> (the "Default" token provider AddDefaultTokenProviders
        // wires up) is internal to Microsoft.AspNetCore.Identity, so it cannot be constructed
        // directly here. Building a real Identity DI container - mirroring how Program.cs wires
        // Identity in the app - resolves a UserManager with a genuinely working token provider
        // without needing access to that internal type.
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

        private static Mock<AuthenticationStateProvider> BuildAuthProvider(bool isAdmin)
        {
            ClaimsIdentity identity = isAdmin
                ? new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Admin") }, "TestAuth")
                : new ClaimsIdentity();

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
            bool isAdmin,
            IEmailService emailService)
        {
            Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
            factoryMock.Setup(f => f.CreateDbContext()).Returns(() => new ApplicationDbContext(options));

            Mock<ICourseService> courseServiceMock = new();
            courseServiceMock.Setup(c => c.GetCoursesAsync()).ReturnsAsync(Result<List<Course>>.Ok([]));

            Mock<ICourseAssignmentService> courseAssignmentServiceMock = new();

            return new SecurityService(
                BuildAuthProvider(isAdmin).Object,
                factoryMock.Object,
                userManager,
                courseAssignmentServiceMock.Object,
                courseServiceMock.Object,
                NullLogger<SecurityService>.Instance,
                new TestNavigationManager(),
                emailService);
        }

        [TestMethod]
        public async Task CreateUserAsync_FirstUser_CreatesWithoutPassword_AndSendsEmail()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);

            Result<UserCreationResult> result = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            });

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.IsNotNull(result.Data);
            Assert.IsTrue(result.Data.EmailSent);
            Assert.AreEqual("jdoe", result.Data.User.UserName);

            UserProfile? persisted = await userManager.FindByIdAsync(result.Data.User.Id);
            Assert.IsNotNull(persisted);
            Assert.IsNull(persisted.PasswordHash);
            Assert.IsNull(persisted.PasswordSetDate);
            Assert.IsNotNull(persisted.InvitedDate);
        }

        [TestMethod]
        public async Task CreateUserAsync_MissingEmail_Fails()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);

            Result<UserCreationResult> result = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = null
            });

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("email", result.Error);
            emailServiceMock.Verify(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task CreateUserAsync_EmailSendFails_StillCreatesUser_ReturnsEmailSentFalse()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>()))
                .ReturnsAsync(Result<bool>.Fail("Email provider unreachable"));

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);

            Result<UserCreationResult> result = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            });

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.IsNotNull(result.Data);
            Assert.IsFalse(result.Data.EmailSent);
            Assert.AreEqual("Email provider unreachable", result.Data.EmailError);

            UserProfile? persisted = await userManager.FindByIdAsync(result.Data.User.Id);
            Assert.IsNotNull(persisted);
        }

        [TestMethod]
        public async Task SetPasswordFromTokenAsync_ValidToken_SetsPasswordAndDate()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);

            Result<UserCreationResult> createResult = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            });

            Assert.IsTrue(createResult.IsSuccess, createResult.Error);
            UserProfile user = createResult.Data!.User;

            string token = await userManager.GeneratePasswordResetTokenAsync(user);

            Result<bool> completeResult = await service.SetPasswordFromTokenAsync(user.Id, token, "NewPassw0rd!");

            Assert.IsTrue(completeResult.IsSuccess, completeResult.Error);

            UserProfile? persisted = await userManager.FindByIdAsync(user.Id);
            Assert.IsNotNull(persisted);
            Assert.IsNotNull(persisted.PasswordSetDate);
            Assert.IsTrue(await userManager.CheckPasswordAsync(persisted, "NewPassw0rd!"));
        }

        [TestMethod]
        public async Task SetPasswordFromTokenAsync_ReusedToken_Fails()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);

            Result<UserCreationResult> createResult = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            });

            UserProfile user = createResult.Data!.User;
            string token = await userManager.GeneratePasswordResetTokenAsync(user);
            Result<bool> firstComplete = await service.SetPasswordFromTokenAsync(user.Id, token, "NewPassw0rd!");
            Assert.IsTrue(firstComplete.IsSuccess, firstComplete.Error);

            // Completing a reset rotates the user's security stamp, which the token's own
            // validation is tied to - so replaying the exact same token must fail even though
            // no explicit "already used" check exists anymore.
            Result<bool> secondComplete = await service.SetPasswordFromTokenAsync(user.Id, token, "AnotherPassw0rd!");

            Assert.IsFalse(secondComplete.IsSuccess);
            Assert.Contains("invalid or has expired", secondComplete.Error);
        }

        [TestMethod]
        public async Task SetPasswordFromTokenAsync_ActiveUser_FreshToken_Succeeds()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);

            Result<UserCreationResult> createResult = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            });

            UserProfile user = createResult.Data!.User;
            string firstToken = await userManager.GeneratePasswordResetTokenAsync(user);
            Result<bool> firstComplete = await service.SetPasswordFromTokenAsync(user.Id, firstToken, "NewPassw0rd!");
            Assert.IsTrue(firstComplete.IsSuccess, firstComplete.Error);

            // A freshly generated token for an already-active user (e.g. an admin-triggered
            // password reset) must still work - this is the whole point of no longer gating
            // on PasswordSetDate.
            UserProfile? active = await userManager.FindByIdAsync(user.Id);
            string secondToken = await userManager.GeneratePasswordResetTokenAsync(active!);
            Result<bool> secondComplete = await service.SetPasswordFromTokenAsync(user.Id, secondToken, "ResetPassw0rd!");

            Assert.IsTrue(secondComplete.IsSuccess, secondComplete.Error);

            UserProfile? persisted = await userManager.FindByIdAsync(user.Id);
            Assert.IsNotNull(persisted);
            Assert.IsTrue(await userManager.CheckPasswordAsync(persisted, "ResetPassw0rd!"));
        }

        [TestMethod]
        public async Task SetPasswordFromTokenAsync_BadToken_Fails()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);

            Result<UserCreationResult> createResult = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            });

            UserProfile user = createResult.Data!.User;

            Result<bool> completeResult = await service.SetPasswordFromTokenAsync(user.Id, "not-a-real-token", "NewPassw0rd!");

            Assert.IsFalse(completeResult.IsSuccess);
            Assert.Contains("invalid or has expired", completeResult.Error);
        }

        [TestMethod]
        public async Task SendSetPasswordLinkAsync_PendingUser_SendsNewEmail()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService service = BuildService(options, userManager, isAdmin: true, emailServiceMock.Object);

            Result<UserCreationResult> createResult = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            });

            UserProfile user = createResult.Data!.User;

            Result<bool> resendResult = await service.SendSetPasswordLinkAsync(user.Id);

            Assert.IsTrue(resendResult.IsSuccess, resendResult.Error);
            emailServiceMock.Verify(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>()), Times.Exactly(2));
        }

        [TestMethod]
        public async Task SendSetPasswordLinkAsync_ActiveUser_StillSendsEmail()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService service = BuildService(options, userManager, isAdmin: true, emailServiceMock.Object);

            Result<UserCreationResult> createResult = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            });

            UserProfile user = createResult.Data!.User;
            string token = await userManager.GeneratePasswordResetTokenAsync(user);
            Result<bool> completeResult = await service.SetPasswordFromTokenAsync(user.Id, token, "NewPassw0rd!");
            Assert.IsTrue(completeResult.IsSuccess, completeResult.Error);

            // The whole point of this feature: an admin can trigger this for an already-active
            // user (e.g. "I forgot my password") and it must still succeed.
            Result<bool> resendResult = await service.SendSetPasswordLinkAsync(user.Id);

            Assert.IsTrue(resendResult.IsSuccess, resendResult.Error);
            emailServiceMock.Verify(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>()), Times.Exactly(2));
        }

        [TestMethod]
        public async Task SendSetPasswordLinkAsync_NonAdmin_Fails()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService adminService = BuildService(options, userManager, isAdmin: true, emailServiceMock.Object);

            Result<UserCreationResult> createResult = await adminService.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            });

            UserProfile user = createResult.Data!.User;

            SecurityService nonAdminService = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);
            Result<bool> resendResult = await nonAdminService.SendSetPasswordLinkAsync(user.Id);

            Assert.IsFalse(resendResult.IsSuccess);
            Assert.Contains("administrator", resendResult.Error);
        }
    }
}

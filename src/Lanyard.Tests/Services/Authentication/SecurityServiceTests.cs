using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Onboarding;
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
using Microsoft.Extensions.Options;
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

        private static Mock<AuthenticationStateProvider> BuildAuthProviderWithRole(string role)
        {
            ClaimsIdentity identity = new(new[] { new Claim(ClaimTypes.Name, "manager"), new Claim(ClaimTypes.Role, role) }, "TestAuth");
            AuthenticationState state = new(new ClaimsPrincipal(identity));

            Mock<AuthenticationStateProvider> mock = new();
            mock.Setup(p => p.GetAuthenticationStateAsync()).ReturnsAsync(state);
            return mock;
        }

        private static async Task AddUserToRoleAsync(DbContextOptions<ApplicationDbContext> options, UserManager<UserProfile> userManager, UserProfile user, string roleName)
        {
            await using ApplicationDbContext context = new(options);

            string normalizedRoleName = roleName.ToUpperInvariant();

            if (!await context.Roles.AnyAsync(r => r.NormalizedName == normalizedRoleName))
            {
                context.Roles.Add(new ApplicationRole
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = roleName,
                    NormalizedName = normalizedRoleName,
                    ConcurrencyStamp = Guid.NewGuid().ToString(),
                    CreatedByUserId = user.Id,
                    CreateDate = DateTime.UtcNow
                });
                await context.SaveChangesAsync();
            }

            IdentityResult addToRoleResult = await userManager.AddToRoleAsync(user, roleName);
            Assert.IsTrue(addToRoleResult.Succeeded, string.Join(", ", addToRoleResult.Errors.Select(e => e.Description)));
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
            IEmailService emailService,
            Mock<ICompanyLocationService>? companyLocationServiceMock = null,
            string publicBaseUrl = "",
            Mock<ICourseService>? courseServiceMock = null,
            Mock<ICourseAssignmentService>? courseAssignmentServiceMock = null,
            Mock<IOnboardingService>? onboardingServiceMock = null)
        {
            return BuildServiceWithAuthProvider(options, userManager, BuildAuthProvider(isAdmin).Object, emailService, companyLocationServiceMock, publicBaseUrl, courseServiceMock, courseAssignmentServiceMock, onboardingServiceMock);
        }

        private static SecurityService BuildServiceWithAuthProvider(
            DbContextOptions<ApplicationDbContext> options,
            UserManager<UserProfile> userManager,
            AuthenticationStateProvider authProvider,
            IEmailService emailService,
            Mock<ICompanyLocationService>? companyLocationServiceMock = null,
            string publicBaseUrl = "",
            Mock<ICourseService>? courseServiceMock = null,
            Mock<ICourseAssignmentService>? courseAssignmentServiceMock = null,
            Mock<IOnboardingService>? onboardingServiceMock = null)
        {
            Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
            factoryMock.Setup(f => f.CreateDbContext()).Returns(() => new ApplicationDbContext(options));

            Mock<ICourseService> resolvedCourseServiceMock = courseServiceMock ?? new Mock<ICourseService>();
            if (courseServiceMock is null)
            {
                resolvedCourseServiceMock.Setup(c => c.GetCoursesAsync(It.IsAny<LocationScope>(), It.IsAny<bool>())).ReturnsAsync(Result<List<Course>>.Ok([]));
            }

            Mock<ICourseAssignmentService> resolvedCourseAssignmentServiceMock = courseAssignmentServiceMock ?? new Mock<ICourseAssignmentService>();

            Mock<ICompanyLocationService> resolvedCompanyLocationServiceMock = companyLocationServiceMock ?? new Mock<ICompanyLocationService>();
            resolvedCompanyLocationServiceMock
                .Setup(c => c.AddUserToLocationAsync(It.IsAny<string>(), It.IsAny<int>()))
                .ReturnsAsync(Result<bool>.Ok(true));
            resolvedCompanyLocationServiceMock
                .Setup(c => c.GetLocationsForUserAsync(It.IsAny<string>()))
                .ReturnsAsync(Result<List<Location>>.Ok([]));
            resolvedCompanyLocationServiceMock
                .Setup(c => c.GetLocationsAsync(It.IsAny<int?>()))
                .ReturnsAsync(Result<List<Location>>.Ok([]));

            Mock<IOnboardingService> resolvedOnboardingServiceMock = onboardingServiceMock ?? new Mock<IOnboardingService>();
            if (onboardingServiceMock is null)
            {
                resolvedOnboardingServiceMock
                    .Setup(o => o.TriggerOnboardingAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Result<bool>.Ok(true));
            }

            return new SecurityService(
                authProvider,
                new CurrentUserAccessor(authProvider),
                factoryMock.Object,
                userManager,
                resolvedCourseAssignmentServiceMock.Object,
                resolvedCourseServiceMock.Object,
                NullLogger<SecurityService>.Instance,
                new TestNavigationManager(),
                emailService,
                resolvedCompanyLocationServiceMock.Object,
                Options.Create(new EmailOptions { PublicBaseUrl = publicBaseUrl }),
                resolvedOnboardingServiceMock.Object);
        }

        [TestMethod]
        public async Task CreateUserAsync_FirstUser_CreatesWithoutPassword_AndSendsEmail()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);

            Result<UserCreationResult> result = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

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
            }, locationIds: [1]);

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("email", result.Error);
            emailServiceMock.Verify(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        }

        [TestMethod]
        public async Task CreateUserAsync_EmailSendFails_StillCreatesUser_ReturnsEmailSentFalse()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Fail("Email provider unreachable"));

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);

            Result<UserCreationResult> result = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.IsNotNull(result.Data);
            Assert.IsFalse(result.Data.EmailSent);
            Assert.AreEqual("Email provider unreachable", result.Data.EmailError);

            UserProfile? persisted = await userManager.FindByIdAsync(result.Data.User.Id);
            Assert.IsNotNull(persisted);
        }

        // Onboarding automation (the welcome email + standing attachments) is decoration on top
        // of account creation, not a precondition for it - a new hire must always get their login,
        // regardless of what goes wrong resolving branding, loading attachments, or sending the
        // email. This is the one behaviour the try/catch around TriggerOnboardingAsync exists to
        // guarantee, so it's asserted directly here rather than trusted to hold implicitly.
        [TestMethod]
        public async Task CreateUserAsync_OnboardingServiceThrows_StillCreatesUserAndSendsSetPasswordEmail()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            Mock<IOnboardingService> onboardingServiceMock = new();
            onboardingServiceMock.Setup(o => o.TriggerOnboardingAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Simulated onboarding failure"));

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object, onboardingServiceMock: onboardingServiceMock);

            Result<UserCreationResult> result = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.IsNotNull(result.Data);
            Assert.IsTrue(result.Data.EmailSent);

            UserProfile? persisted = await userManager.FindByIdAsync(result.Data.User.Id);
            Assert.IsNotNull(persisted);
        }

        [TestMethod]
        public async Task SetPasswordFromTokenAsync_ValidToken_SetsPasswordAndDate()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);

            Result<UserCreationResult> createResult = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

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
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);

            Result<UserCreationResult> createResult = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

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
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);

            Result<UserCreationResult> createResult = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

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
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);

            Result<UserCreationResult> createResult = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

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
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService service = BuildService(options, userManager, isAdmin: true, emailServiceMock.Object);

            Result<UserCreationResult> createResult = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

            UserProfile user = createResult.Data!.User;

            Result<bool> resendResult = await service.SendSetPasswordLinkAsync(user.Id);

            Assert.IsTrue(resendResult.IsSuccess, resendResult.Error);
            emailServiceMock.Verify(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Exactly(2));
        }

        [TestMethod]
        public async Task SendSetPasswordLinkAsync_ActiveUser_StillSendsEmail()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService service = BuildService(options, userManager, isAdmin: true, emailServiceMock.Object);

            Result<UserCreationResult> createResult = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

            UserProfile user = createResult.Data!.User;
            string token = await userManager.GeneratePasswordResetTokenAsync(user);
            Result<bool> completeResult = await service.SetPasswordFromTokenAsync(user.Id, token, "NewPassw0rd!");
            Assert.IsTrue(completeResult.IsSuccess, completeResult.Error);

            // The whole point of this feature: an admin can trigger this for an already-active
            // user (e.g. "I forgot my password") and it must still succeed.
            Result<bool> resendResult = await service.SendSetPasswordLinkAsync(user.Id);

            Assert.IsTrue(resendResult.IsSuccess, resendResult.Error);
            emailServiceMock.Verify(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Exactly(2));
        }

        [TestMethod]
        public async Task SendSetPasswordLinkAsync_NonAdmin_Fails()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService adminService = BuildService(options, userManager, isAdmin: true, emailServiceMock.Object);

            Result<UserCreationResult> createResult = await adminService.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

            UserProfile user = createResult.Data!.User;

            SecurityService nonAdminService = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);
            Result<bool> resendResult = await nonAdminService.SendSetPasswordLinkAsync(user.Id);

            Assert.IsFalse(resendResult.IsSuccess);
            Assert.Contains("administrator", resendResult.Error);
        }

        [TestMethod]
        public async Task CreateUserAsync_BuildsEmailUrlsFromPublicBaseUrl_NotNavigationManagerBaseUri()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            string? capturedSetPasswordUrl = null;
            string? capturedLogoUrl = null;
            string? capturedAccentColor = null;

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .Callback<UserProfile, string, string?, string, string?>((_, setPasswordUrl, logoUrl, accentColor, _) =>
                {
                    capturedSetPasswordUrl = setPasswordUrl;
                    capturedLogoUrl = logoUrl;
                    capturedAccentColor = accentColor;
                })
                .ReturnsAsync(Result<bool>.Ok(true));

            Mock<ICompanyLocationService> companyLocationServiceMock = new();

            SecurityService service = BuildService(
                options,
                userManager,
                isAdmin: false,
                emailServiceMock.Object,
                companyLocationServiceMock,
                publicBaseUrl: "https://public.lanyard.example/");

            Guid logoFileId = Guid.NewGuid();

            // Re-stated after BuildService, which installs a returns-nothing default for this
            // member - the last Moq setup wins, and this one gives the branding branch a company.
            companyLocationServiceMock
                .Setup(c => c.GetLocationsForUserAsync(It.IsAny<string>()))
                .ReturnsAsync(Result<List<Location>>.Ok(
                [
                    new Location
                    {
                        Id = 1,
                        CompanyId = 7,
                        Name = "Ipswich",
                        Company = new Company
                        {
                            Id = 7,
                            Name = "Play2Day",
                            ThemeColorHex = "#C8102E",
                            LogoFileId = logoFileId
                        }
                    }
                ]));

            Result<UserCreationResult> result = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

            Assert.IsTrue(result.IsSuccess, result.Error);

            // Emails are read on someone else's machine, so both URLs must come from the
            // configured public base URL, never from the current request's host.
            Assert.IsNotNull(capturedLogoUrl);
            Assert.AreEqual($"https://public.lanyard.example/api/companies/7/logo?v={logoFileId:N}", capturedLogoUrl);
            Assert.IsNotNull(capturedSetPasswordUrl);
            Assert.StartsWith("https://public.lanyard.example/set-password?userId=", capturedSetPasswordUrl);
            Assert.DoesNotContain("test.lanyard.local", capturedSetPasswordUrl);
            Assert.AreEqual("#C8102E", capturedAccentColor);
        }

        [TestMethod]
        public async Task CreateUserAsync_NoPublicBaseUrl_FallsBackToNavigationManagerBaseUri()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            string? capturedSetPasswordUrl = null;

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .Callback<UserProfile, string, string?, string, string?>((_, setPasswordUrl, _, _, _) => capturedSetPasswordUrl = setPasswordUrl)
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);

            Result<UserCreationResult> result = await service.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.IsNotNull(capturedSetPasswordUrl);
            Assert.StartsWith("https://test.lanyard.local/set-password?userId=", capturedSetPasswordUrl);
        }

        [TestMethod]
        public async Task CreateUserAsync_NoLocationIds_Fails()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);

            Result<UserCreationResult> result = await service.CreateUserAsync(
                new UserProfile { FirstName = "Jane", LastName = "Doe", Email = "jane@example.com" },
                locationIds: []);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("At least one location is required.", result.Error);
            emailServiceMock.Verify(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        }

        [TestMethod]
        public async Task CreateUserAsync_WithLocationIds_AddsUserToEachLocation()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            Mock<ICompanyLocationService> companyLocationServiceMock = new();
            companyLocationServiceMock
                .Setup(c => c.AddUserToLocationAsync(It.IsAny<string>(), It.IsAny<int>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object, companyLocationServiceMock);

            Result<UserCreationResult> result = await service.CreateUserAsync(
                new UserProfile { FirstName = "Jane", LastName = "Doe", Email = "jane@example.com" },
                locationIds: [1, 2]);

            Assert.IsTrue(result.IsSuccess, result.Error);
            companyLocationServiceMock.Verify(x => x.AddUserToLocationAsync(It.IsAny<string>(), 1), Times.Once);
            companyLocationServiceMock.Verify(x => x.AddUserToLocationAsync(It.IsAny<string>(), 2), Times.Once);
        }

        [TestMethod]
        public async Task CreateUserAsync_AutoAssignCourse_MatchingLocationId_IsAssigned()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            Course course = new() { Id = Guid.NewGuid(), Name = "Induction", AutoAssignOnUserCreation = true, LocationId = 1, IsActive = true };
            Mock<ICourseService> courseServiceMock = new();
            courseServiceMock.Setup(c => c.GetCoursesAsync(It.IsAny<LocationScope>(), It.IsAny<bool>())).ReturnsAsync(Result<List<Course>>.Ok([course]));

            Mock<ICourseAssignmentService> courseAssignmentServiceMock = new();
            courseAssignmentServiceMock
                .Setup(c => c.AssignCourseToUsersAsync(course.Id, It.IsAny<List<string>>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<LocationScope>(), It.IsAny<bool>()))
                .ReturnsAsync(Result<BulkAssignResult>.Ok(new BulkAssignResult(1, 0, 0)));

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object,
                courseServiceMock: courseServiceMock, courseAssignmentServiceMock: courseAssignmentServiceMock);

            Result<UserCreationResult> result = await service.CreateUserAsync(
                new UserProfile { FirstName = "Jane", LastName = "Doe", Email = "jane@example.com" },
                locationIds: [1]);

            Assert.IsTrue(result.IsSuccess, result.Error);
            courseAssignmentServiceMock.Verify(c => c.AssignCourseToUsersAsync(course.Id, It.IsAny<List<string>>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<LocationScope>(), It.IsAny<bool>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateUserAsync_AutoAssignCourse_DifferentLocationId_NotShared_IsNotAssigned()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            Course course = new() { Id = Guid.NewGuid(), Name = "Induction", AutoAssignOnUserCreation = true, LocationId = 2, IsShared = false, IsActive = true };
            Mock<ICourseService> courseServiceMock = new();
            courseServiceMock.Setup(c => c.GetCoursesAsync(It.IsAny<LocationScope>(), It.IsAny<bool>())).ReturnsAsync(Result<List<Course>>.Ok([course]));

            Mock<ICourseAssignmentService> courseAssignmentServiceMock = new();

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object,
                courseServiceMock: courseServiceMock, courseAssignmentServiceMock: courseAssignmentServiceMock);

            Result<UserCreationResult> result = await service.CreateUserAsync(
                new UserProfile { FirstName = "Jane", LastName = "Doe", Email = "jane@example.com" },
                locationIds: [1]);

            Assert.IsTrue(result.IsSuccess, result.Error);
            courseAssignmentServiceMock.Verify(c => c.AssignCourseToUsersAsync(It.IsAny<Guid>(), It.IsAny<List<string>>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<LocationScope>(), It.IsAny<bool>()), Times.Never);
        }

        [TestMethod]
        public async Task CreateUserAsync_AutoAssignCourse_SharedSameCompany_IsAssigned()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            Location courseLocation = new() { Id = 2, CompanyId = 10, Name = "Other Location" };
            Course course = new() { Id = Guid.NewGuid(), Name = "Induction", AutoAssignOnUserCreation = true, LocationId = 2, Location = courseLocation, IsShared = true, IsActive = true };
            Mock<ICourseService> courseServiceMock = new();
            courseServiceMock.Setup(c => c.GetCoursesAsync(It.IsAny<LocationScope>(), It.IsAny<bool>())).ReturnsAsync(Result<List<Course>>.Ok([course]));

            Mock<ICourseAssignmentService> courseAssignmentServiceMock = new();
            courseAssignmentServiceMock
                .Setup(c => c.AssignCourseToUsersAsync(course.Id, It.IsAny<List<string>>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<LocationScope>(), It.IsAny<bool>()))
                .ReturnsAsync(Result<BulkAssignResult>.Ok(new BulkAssignResult(1, 0, 0)));

            Mock<ICompanyLocationService> companyLocationServiceMock = new();

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object,
                companyLocationServiceMock, courseServiceMock: courseServiceMock, courseAssignmentServiceMock: courseAssignmentServiceMock);

            // Re-stated after BuildService, which installs a returns-empty default for this
            // member (see BuildServiceWithAuthProvider) - the last Moq setup wins.
            companyLocationServiceMock
                .Setup(c => c.GetLocationsAsync(It.IsAny<int?>()))
                .ReturnsAsync(Result<List<Location>>.Ok([new Location { Id = 1, CompanyId = 10, Name = "User Location" }]));

            Result<UserCreationResult> result = await service.CreateUserAsync(
                new UserProfile { FirstName = "Jane", LastName = "Doe", Email = "jane@example.com" },
                locationIds: [1]);

            Assert.IsTrue(result.IsSuccess, result.Error);
            courseAssignmentServiceMock.Verify(c => c.AssignCourseToUsersAsync(course.Id, It.IsAny<List<string>>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<LocationScope>(), It.IsAny<bool>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateUserAsync_AutoAssignCourse_SharedDifferentCompany_IsNotAssigned()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            Location courseLocation = new() { Id = 2, CompanyId = 20, Name = "Other Company Location" };
            Course course = new() { Id = Guid.NewGuid(), Name = "Induction", AutoAssignOnUserCreation = true, LocationId = 2, Location = courseLocation, IsShared = true, IsActive = true };
            Mock<ICourseService> courseServiceMock = new();
            courseServiceMock.Setup(c => c.GetCoursesAsync(It.IsAny<LocationScope>(), It.IsAny<bool>())).ReturnsAsync(Result<List<Course>>.Ok([course]));

            Mock<ICourseAssignmentService> courseAssignmentServiceMock = new();

            Mock<ICompanyLocationService> companyLocationServiceMock = new();

            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object,
                companyLocationServiceMock, courseServiceMock: courseServiceMock, courseAssignmentServiceMock: courseAssignmentServiceMock);

            // Re-stated after BuildService, which installs a returns-empty default for this
            // member (see BuildServiceWithAuthProvider) - the last Moq setup wins.
            companyLocationServiceMock
                .Setup(c => c.GetLocationsAsync(It.IsAny<int?>()))
                .ReturnsAsync(Result<List<Location>>.Ok([new Location { Id = 1, CompanyId = 10, Name = "User Location" }]));

            Result<UserCreationResult> result = await service.CreateUserAsync(
                new UserProfile { FirstName = "Jane", LastName = "Doe", Email = "jane@example.com" },
                locationIds: [1]);

            Assert.IsTrue(result.IsSuccess, result.Error);
            courseAssignmentServiceMock.Verify(c => c.AssignCourseToUsersAsync(It.IsAny<Guid>(), It.IsAny<List<string>>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<LocationScope>(), It.IsAny<bool>()), Times.Never);
        }

        [TestMethod]
        public async Task CreateUserAsync_Manager_Succeeds()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            // Seed a first user as Admin so the "at least one account exists" role gate is active.
            SecurityService adminService = BuildService(options, userManager, isAdmin: true, emailServiceMock.Object);
            await adminService.CreateUserAsync(new UserProfile { FirstName = "Ada", LastName = "Min", Email = "admin@example.com" }, locationIds: [1]);

            SecurityService managerService = BuildServiceWithAuthProvider(options, userManager, BuildAuthProviderWithRole("Manager").Object, emailServiceMock.Object);

            Result<UserCreationResult> result = await managerService.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

            Assert.IsTrue(result.IsSuccess, result.Error);
        }

        [TestMethod]
        public async Task SendSetPasswordLinkAsync_Manager_Succeeds()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService adminService = BuildService(options, userManager, isAdmin: true, emailServiceMock.Object);
            Result<UserCreationResult> createResult = await adminService.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

            UserProfile user = createResult.Data!.User;

            SecurityService managerService = BuildServiceWithAuthProvider(options, userManager, BuildAuthProviderWithRole("Manager").Object, emailServiceMock.Object);
            Result<bool> resendResult = await managerService.SendSetPasswordLinkAsync(user.Id);

            Assert.IsTrue(resendResult.IsSuccess, resendResult.Error);
        }

        [TestMethod]
        public async Task DeleteUserAsync_Manager_Succeeds()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService adminService = BuildService(options, userManager, isAdmin: true, emailServiceMock.Object);
            Result<UserCreationResult> createResult = await adminService.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

            UserProfile user = createResult.Data!.User;

            SecurityService managerService = BuildServiceWithAuthProvider(options, userManager, BuildAuthProviderWithRole("Manager").Object, emailServiceMock.Object);
            Result<bool> deleteResult = await managerService.DeleteUserAsync(user.Id);

            Assert.IsTrue(deleteResult.IsSuccess, deleteResult.Error);
            Assert.IsNull(await userManager.FindByIdAsync(user.Id));
        }

        [TestMethod]
        public async Task DeleteUserAsync_NonAdminNonManager_Fails()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService adminService = BuildService(options, userManager, isAdmin: true, emailServiceMock.Object);
            Result<UserCreationResult> createResult = await adminService.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

            UserProfile user = createResult.Data!.User;

            SecurityService staffService = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);
            Result<bool> deleteResult = await staffService.DeleteUserAsync(user.Id);

            Assert.IsFalse(deleteResult.IsSuccess);
            Assert.Contains("administrator", deleteResult.Error);
        }

        [TestMethod]
        public async Task DeleteUserAsync_Manager_TargetingAdmin_Fails()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService adminService = BuildService(options, userManager, isAdmin: true, emailServiceMock.Object);
            Result<UserCreationResult> createResult = await adminService.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

            UserProfile targetAdmin = createResult.Data!.User;
            await AddUserToRoleAsync(options, userManager, targetAdmin, "Admin");

            SecurityService managerService = BuildServiceWithAuthProvider(options, userManager, BuildAuthProviderWithRole("Manager").Object, emailServiceMock.Object);
            Result<bool> deleteResult = await managerService.DeleteUserAsync(targetAdmin.Id);

            Assert.IsFalse(deleteResult.IsSuccess, "A Manager must not be able to delete an Admin account.");
            Assert.IsNotNull(await userManager.FindByIdAsync(targetAdmin.Id));
        }

        [TestMethod]
        public async Task UnlockUserAsync_Admin_ClearsLockout()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService adminService = BuildService(options, userManager, isAdmin: true, emailServiceMock.Object);
            Result<UserCreationResult> createResult = await adminService.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

            UserProfile user = createResult.Data!.User;
            await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(15));
            await userManager.AccessFailedAsync(user);

            Result<bool> unlockResult = await adminService.UnlockUserAsync(user.Id);

            Assert.IsTrue(unlockResult.IsSuccess, unlockResult.Error);

            UserProfile? updated = await userManager.FindByIdAsync(user.Id);
            Assert.IsNotNull(updated);
            Assert.IsFalse(await userManager.IsLockedOutAsync(updated));
            Assert.AreEqual(0, updated.AccessFailedCount);
        }

        [TestMethod]
        public async Task UnlockUserAsync_NonAdminNonManager_Fails()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService adminService = BuildService(options, userManager, isAdmin: true, emailServiceMock.Object);
            Result<UserCreationResult> createResult = await adminService.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

            UserProfile user = createResult.Data!.User;
            await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(15));

            SecurityService staffService = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);
            Result<bool> unlockResult = await staffService.UnlockUserAsync(user.Id);

            Assert.IsFalse(unlockResult.IsSuccess);
            Assert.Contains("administrator", unlockResult.Error);
        }

        [TestMethod]
        public async Task UnlockUserAsync_Manager_TargetingAdmin_Fails()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService adminService = BuildService(options, userManager, isAdmin: true, emailServiceMock.Object);
            Result<UserCreationResult> createResult = await adminService.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

            UserProfile targetAdmin = createResult.Data!.User;
            await AddUserToRoleAsync(options, userManager, targetAdmin, "Admin");
            await userManager.SetLockoutEndDateAsync(targetAdmin, DateTimeOffset.UtcNow.AddMinutes(15));

            SecurityService managerService = BuildServiceWithAuthProvider(options, userManager, BuildAuthProviderWithRole("Manager").Object, emailServiceMock.Object);
            Result<bool> unlockResult = await managerService.UnlockUserAsync(targetAdmin.Id);

            Assert.IsFalse(unlockResult.IsSuccess, "A Manager must not be able to unlock an Admin account.");

            UserProfile? stillLocked = await userManager.FindByIdAsync(targetAdmin.Id);
            Assert.IsNotNull(stillLocked);
            Assert.IsTrue(await userManager.IsLockedOutAsync(stillLocked));
        }

        [TestMethod]
        public async Task SendSetPasswordLinkAsync_Manager_TargetingAdmin_Fails()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService adminService = BuildService(options, userManager, isAdmin: true, emailServiceMock.Object);
            Result<UserCreationResult> createResult = await adminService.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

            UserProfile targetAdmin = createResult.Data!.User;
            await AddUserToRoleAsync(options, userManager, targetAdmin, "Admin");

            SecurityService managerService = BuildServiceWithAuthProvider(options, userManager, BuildAuthProviderWithRole("Manager").Object, emailServiceMock.Object);
            Result<bool> resendResult = await managerService.SendSetPasswordLinkAsync(targetAdmin.Id);

            Assert.IsFalse(resendResult.IsSuccess, "A Manager must not be able to trigger a password reset for an Admin account.");
        }

        [TestMethod]
        public async Task DeleteUserAsync_Admin_CanTargetAdmin_Succeeds()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            Mock<IEmailService> emailServiceMock = new();
            emailServiceMock.Setup(e => e.SendSetPasswordEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            SecurityService adminService = BuildService(options, userManager, isAdmin: true, emailServiceMock.Object);
            Result<UserCreationResult> createResult = await adminService.CreateUserAsync(new UserProfile
            {
                FirstName = "Jane",
                LastName = "Doe",
                Email = "jane@example.com"
            }, locationIds: [1]);

            UserProfile targetAdmin = createResult.Data!.User;
            await AddUserToRoleAsync(options, userManager, targetAdmin, "Admin");

            Result<bool> deleteResult = await adminService.DeleteUserAsync(targetAdmin.Id);

            Assert.IsTrue(deleteResult.IsSuccess, deleteResult.Error);
            Assert.IsNull(await userManager.FindByIdAsync(targetAdmin.Id));
        }

        [TestMethod]
        public async Task GetActiveUsersInLocationAsync_ReturnsOnlyMembersOfThatLocation()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            UserManager<UserProfile> userManager = BuildUserManager(options);

            UserProfile ipswichUser = new() { FirstName = "Jane", LastName = "Doe", Email = "jane@example.com", UserName = "jdoe" };
            UserProfile otherSiteUser = new() { FirstName = "John", LastName = "Smith", Email = "john@example.com", UserName = "jsmith" };

            Assert.IsTrue((await userManager.CreateAsync(ipswichUser)).Succeeded);
            Assert.IsTrue((await userManager.CreateAsync(otherSiteUser)).Succeeded);

            await using (ApplicationDbContext seedCtx = new(options))
            {
                seedCtx.UserLocationMemberships.Add(new UserLocationMembership { UserId = ipswichUser.Id, LocationId = 1, CreateDate = DateTime.UtcNow });
                seedCtx.UserLocationMemberships.Add(new UserLocationMembership { UserId = otherSiteUser.Id, LocationId = 2, CreateDate = DateTime.UtcNow });
                await seedCtx.SaveChangesAsync();
            }

            Mock<IEmailService> emailServiceMock = new();
            SecurityService service = BuildService(options, userManager, isAdmin: false, emailServiceMock.Object);

            IEnumerable<UserProfile> result = await service.GetActiveUsersInLocationAsync(1);

            UserProfile onlyResult = result.Single();
            Assert.AreEqual(ipswichUser.Id, onlyResult.Id);
        }
    }
}

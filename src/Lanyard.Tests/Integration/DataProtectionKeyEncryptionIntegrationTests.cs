using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// Security Issue 9.13: with DataProtection:CertificateBase64 set, the key ring in the database is
// stored encrypted, and signing in (which needs the key ring) still works.
[TestClass]
public class DataProtectionKeyEncryptionIntegrationTests
{
    private static string NewCertificateBase64(string password)
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new("CN=Lanyard Data Protection test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        return Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, password));
    }

    [TestMethod]
    public async Task WithACertificate_TheKeyRingIsEncrypted_AndSignInStillWorks()
    {
        using CustomWebApplicationFactory baseFactory = new();
        using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DataProtection:CertificateBase64", NewCertificateBase64("test-password"));
            builder.UseSetting("DataProtection:CertificatePassword", "test-password");
        });

        HttpClient client = factory.CreateClient();
        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginDto { Username = "admin", Password = CustomWebApplicationFactory.SeedAdminPassword, LocationId = ApplicationDbContext.SeedIpswichLocationId });
        Assert.IsTrue(login.IsSuccessStatusCode, $"{(int)login.StatusCode}");

        HttpResponseMessage page = await client.GetAsync("/manage/dashboards");
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, "The sign-in cookie must still be readable.");

        using IServiceScope scope = factory.Services.CreateScope();
        ApplicationDbContext ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        List<string?> keys = ctx.DataProtectionKeys.Select(x => x.Xml).ToList();

        Assert.IsTrue(keys.Count > 0, "Signing in should have created a key.");
        Assert.IsTrue(keys.All(xml => xml!.Contains("encryptedSecret")), "Every key must be stored encrypted.");
    }
}

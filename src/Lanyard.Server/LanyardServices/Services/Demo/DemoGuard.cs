using Lanyard.Application.Services.Tenancy;

namespace Lanyard.Application.Services.Demo;

// "Is the person using this circuit/request in the demo?" - scoped, for the lockdowns on things a
// shared demo mustn't allow (passwords, 2FA, uploads, notifications) and for the demo banner.
public interface IDemoGuard
{
    Task<bool> IsDemoSessionAsync();
}

public sealed class DemoGuard(ITenantContext tenant, IDemoDirectory directory) : IDemoGuard
{
    public const string NotInDemoMessage = "That isn't available in the demo.";

    public Task<bool> IsDemoSessionAsync() =>
        tenant.IsSystem ? Task.FromResult(false) : directory.IsDemoCompanyAsync(tenant.CompanyId);
}

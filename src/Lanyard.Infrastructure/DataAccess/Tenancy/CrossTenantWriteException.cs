namespace Lanyard.Infrastructure.DataAccess.Tenancy;

public class CrossTenantWriteException(string message) : InvalidOperationException(message);

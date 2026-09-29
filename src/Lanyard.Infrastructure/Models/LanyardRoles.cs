namespace Lanyard.Infrastructure.Models;

public static class LanyardRoles
{
    // Lanyard's own operators. Unlike Admin (which is admin of the company the user signed in to),
    // a PlatformAdmin may sign in to any company's locations. Only another PlatformAdmin can grant
    // or remove it.
    public const string PlatformAdmin = "PlatformAdmin";
}

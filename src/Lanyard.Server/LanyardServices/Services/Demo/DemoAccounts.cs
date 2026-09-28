namespace Lanyard.Application.Services.Demo;

// The three accounts the homepage's demo buttons sign visitors in as. Fixed ids, so sessions
// survive the nightly reset and the lockdowns can recognise them. They have no password: the
// only way in is the demo login endpoint.
public static class DemoAccounts
{
    public const string AdminUserId = "lanyard-demo-admin";
    public const string ManagerUserId = "lanyard-demo-manager";
    public const string StaffUserId = "lanyard-demo-staff";

    public static readonly IReadOnlySet<string> LoginUserIds = new HashSet<string> { AdminUserId, ManagerUserId, StaffUserId };

    // role key (as used in /api/auth/demo-login?role=) -> account
    public static string? UserIdForRole(string? role) => role?.ToLowerInvariant() switch
    {
        "admin" => AdminUserId,
        "manager" => ManagerUserId,
        "staff" => StaffUserId,
        _ => null,
    };
}

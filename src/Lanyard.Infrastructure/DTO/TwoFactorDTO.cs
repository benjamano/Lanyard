namespace Lanyard.Infrastructure.DTO
{
    public class TwoFactorStatusDto
    {
        public bool IsEnabled { get; set; }
        public bool HasAuthenticator { get; set; }
        public int RecoveryCodesRemaining { get; set; }

        // Derived rather than independently settable: the two 2FA methods are mutually exclusive,
        // and HasEmail is fully determined by IsEnabled/HasAuthenticator, so making it a plain
        // settable bool would let a caller construct (or a future bug leave) an inconsistent
        // combination the UI has no way to detect.
        public bool HasEmail => IsEnabled && !HasAuthenticator;

        // The user's company makes 2FA compulsory, so it can't be switched off from here.
        public bool IsRequiredByCompany { get; set; }
    }

    public class TwoFactorPolicyDto
    {
        public int CompanyId { get; set; }
        public required string CompanyName { get; set; }
        public bool IsRequired { get; set; }
        public DateTime? RequiredSince { get; set; }
        public string? RequiredByName { get; set; }
        public List<TwoFactorUserStatusDto> Users { get; set; } = [];

        public int UsersWithTwoFactor => Users.Count(x => x.IsEnabled);
    }

    public record TwoFactorUserStatusDto(string UserId, string Name, bool IsEnabled);

    public class AuthenticatorEnrollmentDto
    {
        public required string SharedKey { get; set; }
        public required string AuthenticatorUri { get; set; }
        public required string QrCodeDataUri { get; set; }
    }
}

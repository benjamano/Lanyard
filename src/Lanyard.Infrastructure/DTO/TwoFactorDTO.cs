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
    }

    public class AuthenticatorEnrollmentDto
    {
        public required string SharedKey { get; set; }
        public required string AuthenticatorUri { get; set; }
        public required string QrCodeDataUri { get; set; }
    }
}

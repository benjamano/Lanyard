namespace Lanyard.Infrastructure.DTO
{
    public class TwoFactorStatusDto
    {
        public bool IsEnabled { get; set; }
        public bool HasAuthenticator { get; set; }
        public bool HasEmail { get; set; }
        public int RecoveryCodesRemaining { get; set; }
    }

    public class AuthenticatorEnrollmentDto
    {
        public required string SharedKey { get; set; }
        public required string AuthenticatorUri { get; set; }
        public required string QrCodeDataUri { get; set; }
    }
}

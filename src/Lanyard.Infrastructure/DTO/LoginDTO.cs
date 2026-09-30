using System.ComponentModel.DataAnnotations;

namespace Lanyard.Infrastructure.DTO
{
    public class LoginDto
    {
        [Required]
        public string Username { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        public bool RememberMe { get; set; } = false;

        public int? LocationId { get; set; }
    }

    // What the browser's navigator.credentials.create() produced, JSON-serialised, when adding a
    // passkey. Capped well above any real credential so an oversized body is rejected up front.
    public class PasskeyCredentialDto
    {
        [Required]
        [MaxLength(64 * 1024)]
        public string CredentialJson { get; set; } = string.Empty;
    }
}

using System.Globalization;
using Microsoft.AspNetCore.DataProtection;

namespace Lanyard.API.Contact
{
    // A signed "this form was shown at <time>" stamp, put in the homepage contact form when it renders
    // and checked by ContactController. A script posting straight to /api/contact has no stamp, and one
    // that fills the form in faster than a person could is dropped - both without an email being sent.
    public static class ContactFormToken
    {
        private const string Purpose = "Lanyard.ContactForm.v1";

        // Nobody reads the form, types a message and presses Send in under this.
        public static readonly TimeSpan MinimumFillTime = TimeSpan.FromSeconds(3);

        // Long enough for a tab left open all day; after that the page needs a reload.
        public static readonly TimeSpan MaximumAge = TimeSpan.FromHours(24);

        public enum Verdict
        {
            Ok,
            Invalid,
            TooFast,
            Expired,
        }

        public static string Create(IDataProtectionProvider provider, DateTimeOffset now) =>
            provider.CreateProtector(Purpose).Protect(now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

        public static Verdict Check(IDataProtectionProvider provider, string? token, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return Verdict.Invalid;
            }

            long issuedMs;

            try
            {
                if (!long.TryParse(provider.CreateProtector(Purpose).Unprotect(token), NumberStyles.None, CultureInfo.InvariantCulture, out issuedMs))
                {
                    return Verdict.Invalid;
                }
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return Verdict.Invalid;
            }

            TimeSpan age = now - DateTimeOffset.FromUnixTimeMilliseconds(issuedMs);

            if (age < MinimumFillTime)
            {
                return Verdict.TooFast;
            }

            return age > MaximumAge ? Verdict.Expired : Verdict.Ok;
        }
    }
}

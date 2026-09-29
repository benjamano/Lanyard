using System.Net.Mail;

namespace Lanyard.Application.Services.Email;

// What someone sent from the public homepage's contact form. Emailed to the site's contact address
// with the visitor as the reply-to, so answering it is just pressing Reply.
public sealed record ContactEnquiry(string Name, string Email, string? Venue, string Message)
{
    public const int NameMaxLength = 100;
    public const int EmailMaxLength = 254;
    public const int VenueMaxLength = 150;
    public const int MessageMinLength = 10;
    public const int MessageMaxLength = 4000;

    // The enquiry, trimmed, or what to tell the visitor when it can't be sent as it is.
    public static (ContactEnquiry? Enquiry, string? Error) Create(string? name, string? email, string? venue, string? message)
    {
        name = name?.Trim() ?? string.Empty;
        email = email?.Trim() ?? string.Empty;
        venue = string.IsNullOrWhiteSpace(venue) ? null : venue.Trim();
        message = message?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            return (null, "Please tell us your name.");
        }

        if (name.Length > NameMaxLength || name.Contains('\n') || name.Contains('\r'))
        {
            return (null, $"Your name needs to be a single line of up to {NameMaxLength} characters.");
        }

        if (!IsEmailAddress(email))
        {
            return (null, "Please enter an email address we can reply to.");
        }

        if (venue is not null && (venue.Length > VenueMaxLength || venue.Contains('\n') || venue.Contains('\r')))
        {
            return (null, $"Your venue's name needs to be a single line of up to {VenueMaxLength} characters.");
        }

        if (message.Length < MessageMinLength)
        {
            return (null, "Please write a little more in your message.");
        }

        if (message.Length > MessageMaxLength)
        {
            return (null, $"Your message is too long - please keep it under {MessageMaxLength} characters.");
        }

        return (new ContactEnquiry(name, email, venue, message), null);
    }

    private static bool IsEmailAddress(string email) =>
        email.Length <= EmailMaxLength
        && !email.Any(char.IsWhiteSpace)
        && MailAddress.TryCreate(email, out MailAddress? address)
        && address.Address == email
        && address.Host.Contains('.');
}

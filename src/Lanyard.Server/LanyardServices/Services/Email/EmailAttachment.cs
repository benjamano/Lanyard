namespace Lanyard.Application.Services.Email;

/// <summary>
/// A file to attach to an outgoing email. <see cref="Content"/> is the raw bytes;
/// base64 encoding for the wire happens once, inside EmailService's Resend call.
/// </summary>
public record EmailAttachment(string FileName, byte[] Content);

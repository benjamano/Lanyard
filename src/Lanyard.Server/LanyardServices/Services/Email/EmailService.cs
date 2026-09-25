using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lanyard.Application.Services.Email;

public class EmailService : IEmailService
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<EmailOptions> _options;
    private readonly ILogger<EmailService> _logger;

    public EmailService(HttpClient httpClient, IOptions<EmailOptions> options, ILogger<EmailService> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<Result<bool>> SendSetPasswordEmailAsync(UserProfile user, string setPasswordUrl, string? logoUrl, string accentColorHex, string? locationName)
    {
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return Result<bool>.Fail("User has no email address to send a link to.");
        }

        string html = BuildSetPasswordHtml(user.UserName ?? user.Email, setPasswordUrl, ResolveLogoUrl(logoUrl, setPasswordUrl), accentColorHex, locationName);

        return await SendResendEmailAsync(user.Id, user.Email, "Set your Lanyard password", html);
    }

    public async Task<Result<bool>> SendCourseRecurrenceReminderEmailAsync(UserProfile user, string courseName, string trainingUrl, string? logoUrl, string accentColorHex)
    {
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return Result<bool>.Fail("User has no email address to send a link to.");
        }

        string html = BuildRecurrenceReminderHtml(user.GetGreetingName(), courseName, trainingUrl, ResolveLogoUrl(logoUrl), accentColorHex);

        return await SendResendEmailAsync(user.Id, user.Email, $"Time to retake: {courseName}", html);
    }

    public async Task<Result<bool>> SendTwoFactorCodeEmailAsync(UserProfile user, string code)
    {
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return Result<bool>.Fail("User has no email address to send a code to.");
        }

        string html = BuildTwoFactorCodeHtml(code, ResolveLogoUrl(null));

        return await SendResendEmailAsync(user.Id, user.Email, "Your Lanyard sign-in code", html);
    }

    public async Task<Result<bool>> SendTrainingAssignedEmailAsync(UserProfile user, string courseName, DateTime? dueDate, string trainingUrl, string? logoUrl, string accentColorHex)
    {
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return Result<bool>.Fail("User has no email address to send a link to.");
        }

        string html = BuildTrainingAssignedHtml(user.GetGreetingName(), courseName, dueDate, trainingUrl, ResolveLogoUrl(logoUrl), accentColorHex);

        return await SendResendEmailAsync(user.Id, user.Email, $"New training assigned: {courseName}", html);
    }

    public async Task<Result<bool>> SendTrainingDueSoonEmailAsync(UserProfile user, string courseName, DateTime dueDate, string trainingUrl, string? logoUrl, string accentColorHex)
    {
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return Result<bool>.Fail("User has no email address to send a link to.");
        }

        string html = BuildTrainingDueSoonHtml(user.GetGreetingName(), courseName, dueDate, trainingUrl, ResolveLogoUrl(logoUrl), accentColorHex);

        return await SendResendEmailAsync(user.Id, user.Email, $"Training due soon: {courseName}", html);
    }

    public async Task<Result<bool>> SendCourseCompletionCertificateEmailAsync(UserProfile user, string courseName, byte[] certificatePdf, string? logoUrl, string accentColorHex)
    {
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return Result<bool>.Fail("User has no email address to send a certificate to.");
        }

        string html = BuildCourseCompletionCertificateHtml(user.GetGreetingName(), courseName, ResolveLogoUrl(logoUrl), accentColorHex);

        EmailAttachment attachment = new(BuildCertificateFileName(courseName), certificatePdf);

        return await SendResendEmailAsync(user.Id, user.Email, $"Certificate: {courseName}", html, [attachment]);
    }

    public async Task<Result<bool>> SendStaffDocumentExpiryReminderEmailAsync(UserProfile user, string documentTypeName, DateTime expiryDate, int daysBeforeExpiry, string? logoUrl, string accentColorHex)
    {
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return Result<bool>.Fail("User has no email address to send a link to.");
        }

        string html = BuildStaffDocumentExpiryReminderHtml(user.GetGreetingName(), documentTypeName, expiryDate, daysBeforeExpiry, ResolveLogoUrl(logoUrl), accentColorHex);

        return await SendResendEmailAsync(user.Id, user.Email, $"Expiring soon: {documentTypeName}", html);
    }

    public async Task<Result<bool>> SendOnboardingWelcomeEmailAsync(UserProfile user, string subject, string bodyHtml, string? logoUrl, string accentColorHex, IReadOnlyList<EmailAttachment> attachments)
    {
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return Result<bool>.Fail("User has no email address to send a welcome email to.");
        }

        string html = BuildOnboardingWelcomeHtml(user.GetGreetingName(), bodyHtml, ResolveLogoUrl(logoUrl), accentColorHex);

        return await SendResendEmailAsync(user.Id, user.Email, subject, html, attachments);
    }

    // Resend takes the filename verbatim into the Content-Disposition header, so anything
    // that would need quoting or escaping there is stripped rather than passed through.
    private static string BuildCertificateFileName(string courseName)
    {
        string safeCourseName = new string(courseName.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_').ToArray()).Trim();

        return string.IsNullOrWhiteSpace(safeCourseName) ? "Certificate.pdf" : $"{safeCourseName} Certificate.pdf";
    }

    public async Task<Result<bool>> SendRotaChangedEmailAsync(UserProfile user, string locationName, IReadOnlyList<ShiftEmailLine> added, IReadOnlyList<ShiftEmailLine> changed, IReadOnlyList<ShiftEmailLine> removed, string myShiftsUrl, string? logoUrl, string accentColorHex)
    {
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return Result<bool>.Fail("User has no email address to send their rota to.");
        }

        // "Your shifts" when it's all new (the usual monthly publish); "has changed" as soon as
        // anything they'd already been told about moved or went, so a last-minute change stands out.
        bool onlyNew = changed.Count == 0 && removed.Count == 0;
        string subject = onlyNew
            ? $"Your shifts at {locationName}"
            : $"Your rota at {locationName} has changed";

        string html = BuildRotaChangedHtml(user.GetGreetingName(), locationName, added, changed, removed, onlyNew, myShiftsUrl, logoUrl, accentColorHex);

        return await SendResendEmailAsync(user.Id, user.Email, subject, html);
    }

    public async Task<Result<bool>> SendShiftReminderEmailAsync(UserProfile user, string locationName, ShiftEmailLine shift, string dayLabel, string myShiftsUrl, string? logoUrl, string accentColorHex)
    {
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return Result<bool>.Fail("User has no email address to send a reminder to.");
        }

        string html = BuildShiftReminderHtml(user.GetGreetingName(), locationName, shift, dayLabel, myShiftsUrl, logoUrl, accentColorHex);

        return await SendResendEmailAsync(user.Id, user.Email, $"Reminder: you're working {dayLabel}, {shift.TimeRange}", html);
    }

    public async Task<Result<bool>> SendTimeOffRequestedEmailAsync(UserProfile manager, string requesterName, string typeName, DateOnly start, DateOnly end, string amount, string? notes, string approvalsUrl, string? logoUrl, string accentColorHex)
    {
        if (string.IsNullOrWhiteSpace(manager.Email))
        {
            return Result<bool>.Fail("User has no email address to send the request to.");
        }

        string html = BuildTimeOffRequestedHtml(manager.GetGreetingName(), requesterName, typeName, start, end, amount, notes, approvalsUrl, logoUrl, accentColorHex);

        return await SendResendEmailAsync(manager.Id, manager.Email, $"{requesterName} has asked for time off", html);
    }

    public async Task<Result<bool>> SendTimeOffDecisionEmailAsync(UserProfile user, string typeName, DateOnly start, DateOnly end, TimeOffEmailOutcome outcome, string? reason, string? decidedByName, string myTimeOffUrl, string? logoUrl, string accentColorHex)
    {
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return Result<bool>.Fail("User has no email address to send the decision to.");
        }

        string subject = outcome switch
        {
            TimeOffEmailOutcome.Approved => "Your time off is approved",
            TimeOffEmailOutcome.Rejected => "Your time off wasn't approved",
            TimeOffEmailOutcome.Withdrawn => "Your approved time off has been withdrawn",
            TimeOffEmailOutcome.CutShort => "Your time off has been cut short",
            _ => "Time off has been recorded for you"
        };

        string html = BuildTimeOffDecisionHtml(user.GetGreetingName(), typeName, start, end, outcome, reason, decidedByName, myTimeOffUrl, logoUrl, accentColorHex);

        return await SendResendEmailAsync(user.Id, user.Email, subject, html);
    }

    // Single decision point for the Resend HTTP call - the config check, request shape,
    // auth header, and error handling used to be copy-pasted into each Send*Async method above.
    private async Task<Result<bool>> SendResendEmailAsync(
        string userId, string toEmail, string subject, string html,
        IReadOnlyList<EmailAttachment>? attachments = null)
    {
        try
        {
            EmailOptions config = _options.Value;

            if (string.IsNullOrWhiteSpace(config.ResendApiKey) || string.IsNullOrWhiteSpace(config.FromAddress))
            {
                return Result<bool>.Fail("Email is not configured (missing Resend API key or From address).");
            }

            string from = $"{config.FromName} <{config.FromAddress}>";

            // Two shapes rather than one object with a null property: Resend rejects a null
            // "attachments" key, and keeping the no-attachment branch byte-identical to what
            // the five pre-existing callers have always sent means adding this can't change
            // their behaviour at all.
            object payload = attachments is null || attachments.Count == 0
                ? new
                {
                    from,
                    to = new[] { toEmail },
                    subject,
                    html
                }
                : new
                {
                    from,
                    to = new[] { toEmail },
                    subject,
                    html,
                    attachments = attachments
                        .Select(x => new { filename = x.FileName, content = Convert.ToBase64String(x.Content) })
                        .ToArray()
                };

            HttpRequestMessage request = new(HttpMethod.Post, "emails")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ResendApiKey);

            HttpResponseMessage response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                _logger.LogError("Resend email send failed ({StatusCode}): {Body}", response.StatusCode, body);
                return Result<bool>.Fail($"Email provider returned {(int)response.StatusCode}.");
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception sending email to {UserId}", userId);
            return Result<bool>.Fail(ex.Message);
        }
    }

    // Emails carry no "Lanyard" heading - the company's own logo is the header when it has one,
    // and the Lanyard logo (wwwroot/logo.png) stands in only when it doesn't. The fallback needs an
    // absolute, externally reachable URL, so it comes from PublicBaseUrl, or failing that the origin
    // of a link the caller already built (the invite email builds its link off the live request
    // when PublicBaseUrl is unset). With neither, the email simply has no header image.
    private string? ResolveLogoUrl(string? companyLogoUrl, string? linkUrl = null)
    {
        if (companyLogoUrl is not null)
        {
            return companyLogoUrl;
        }

        string? baseUrl = _options.Value.PublicBaseUrl;

        if (string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(linkUrl, UriKind.Absolute, out Uri? linkUri))
        {
            baseUrl = linkUri.GetLeftPart(UriPartial.Authority);
        }

        return string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl.TrimEnd('/')}/logo.png";
    }

    private static string BuildLogoHtml(string? logoUrl)
    {
        return logoUrl is not null
            ? $"""<img src="{logoUrl}" alt="Logo" style="max-height: 48px; display: block; margin-bottom: 12px;" />"""
            : string.Empty;
    }

    private static string BuildTwoFactorCodeHtml(string code, string? logoUrl)
    {
        string logoHtml = BuildLogoHtml(logoUrl);

        return $"""
        <div style="font-family: Arial, sans-serif; max-width: 480px; margin: 0 auto;">
          {logoHtml}
          <p>Your sign-in code is:</p>
          <p style="font-size: 28px; font-weight: bold; letter-spacing: 4px;">{WebUtility.HtmlEncode(code)}</p>
          <p style="color: #666; font-size: 13px;">This code expires shortly. If you didn't try to sign in, you can ignore this email.</p>
        </div>
        """;
    }

    private static string BuildRecurrenceReminderHtml(string greetingName, string courseName, string trainingUrl, string? logoUrl, string accentColorHex)
    {
        string logoHtml = BuildLogoHtml(logoUrl);

        return $"""
        <div style="font-family: Arial, sans-serif; max-width: 480px; margin: 0 auto;">
          {logoHtml}
          <p>Hi {WebUtility.HtmlEncode(greetingName)},</p>
          <p>It's time to retake the training course <strong>{WebUtility.HtmlEncode(courseName)}</strong>. Your previous
             completion has expired and needs to be renewed.</p>
          <p>
            <a href="{trainingUrl}" style="display: inline-block; padding: 12px 24px; background: {accentColorHex}; color: #fff; text-decoration: none; border-radius: 4px;">
              Start Training
            </a>
          </p>
        </div>
        """;
    }

    private static string BuildTrainingAssignedHtml(string greetingName, string courseName, DateTime? dueDate, string trainingUrl, string? logoUrl, string accentColorHex)
    {
        string logoHtml = BuildLogoHtml(logoUrl);

        string dueDateHtml = dueDate is not null
            ? $"""<p>It is due by <strong>{dueDate.Value.Date:d MMMM yyyy}</strong>.</p>"""
            : string.Empty;

        return $"""
        <div style="font-family: Arial, sans-serif; max-width: 480px; margin: 0 auto;">
          {logoHtml}
          <p>Hi {WebUtility.HtmlEncode(greetingName)},</p>
          <p>You've been assigned a new training course: <strong>{WebUtility.HtmlEncode(courseName)}</strong>.</p>
          {dueDateHtml}
          <p>
            <a href="{trainingUrl}" style="display: inline-block; padding: 12px 24px; background: {accentColorHex}; color: #fff; text-decoration: none; border-radius: 4px;">
              Start Training
            </a>
          </p>
        </div>
        """;
    }

    private static string BuildTrainingDueSoonHtml(string greetingName, string courseName, DateTime dueDate, string trainingUrl, string? logoUrl, string accentColorHex)
    {
        string logoHtml = BuildLogoHtml(logoUrl);

        return $"""
        <div style="font-family: Arial, sans-serif; max-width: 480px; margin: 0 auto;">
          {logoHtml}
          <p>Hi {WebUtility.HtmlEncode(greetingName)},</p>
          <p>Your training course <strong>{WebUtility.HtmlEncode(courseName)}</strong> is due soon, by
             <strong>{dueDate.Date:d MMMM yyyy}</strong>.</p>
          <p>
            <a href="{trainingUrl}" style="display: inline-block; padding: 12px 24px; background: {accentColorHex}; color: #fff; text-decoration: none; border-radius: 4px;">
              Continue Training
            </a>
          </p>
        </div>
        """;
    }

    private static string BuildCourseCompletionCertificateHtml(string greetingName, string courseName, string? logoUrl, string accentColorHex)
    {
        string logoHtml = BuildLogoHtml(logoUrl);

        return $"""
        <div style="font-family: Arial, sans-serif; max-width: 480px; margin: 0 auto;">
          {logoHtml}
          <p>Hi {WebUtility.HtmlEncode(greetingName)},</p>
          <p>Congratulations - you've passed <strong>{WebUtility.HtmlEncode(courseName)}</strong>.</p>
          <p>Your certificate of completion is attached to this email as a PDF. You can also
             download it again at any time from the My Training page.</p>
          <p style="border-left: 4px solid {accentColorHex}; padding-left: 12px; color: #666; font-size: 13px;">
            No further action is needed.
          </p>
        </div>
        """;
    }

    private static string BuildStaffDocumentExpiryReminderHtml(string greetingName, string documentTypeName, DateTime expiryDate, int daysBeforeExpiry, string? logoUrl, string accentColorHex)
    {
        string logoHtml = BuildLogoHtml(logoUrl);

        return $"""
        <div style="font-family: Arial, sans-serif; max-width: 480px; margin: 0 auto;">
          {logoHtml}
          <p>Hi {WebUtility.HtmlEncode(greetingName)},</p>
          <p>Your <strong>{WebUtility.HtmlEncode(documentTypeName)}</strong> is expiring in
             <strong>{daysBeforeExpiry} day{(daysBeforeExpiry == 1 ? "" : "s")}</strong>, on
             <strong>{expiryDate.Date:d MMMM yyyy}</strong>. Please arrange a renewal and upload the
             updated document to your staff profile.</p>
          <p style="border-left: 4px solid {accentColorHex}; padding-left: 12px; color: #666; font-size: 13px;">
            If this has already been renewed, you can disregard this reminder once the new document is uploaded.
          </p>
        </div>
        """;
    }

    // The admin-authored body is arbitrary Quill HTML - fundamentally untrusted the moment it's
    // read back out of storage. Sanitized here, right before it leaves the app in an email, not
    // at save time - matches RenderTextAreaWidget.razor's "sanitize at the point it becomes live
    // markup" precedent, and this is a stronger case for it since the output goes to third-party
    // mail clients rather than just back into our own page.
    private static readonly Ganss.Xss.HtmlSanitizer _welcomeEmailSanitizer = new();

    private static string BuildOnboardingWelcomeHtml(string greetingName, string bodyHtml, string? logoUrl, string accentColorHex)
    {
        string logoHtml = BuildLogoHtml(logoUrl);

        string safeBodyHtml = _welcomeEmailSanitizer.Sanitize(bodyHtml);

        return $"""
        <div style="font-family: Arial, sans-serif; max-width: 480px; margin: 0 auto;">
          {logoHtml}
          <p>Hi {WebUtility.HtmlEncode(greetingName)},</p>
          <div style="border-left: 4px solid {accentColorHex}; padding-left: 12px;">
            {safeBodyHtml}
          </div>
        </div>
        """;
    }

    private static string BuildSetPasswordHtml(string username, string setPasswordUrl, string? logoUrl, string accentColorHex, string? locationName)
    {
        string logoHtml = BuildLogoHtml(logoUrl);

        string locationHtml = locationName is not null
            ? $"""<p>Log in at: <strong>{WebUtility.HtmlEncode(locationName)}</strong></p>"""
            : string.Empty;

        return $"""
        <div style="font-family: Arial, sans-serif; max-width: 480px; margin: 0 auto;">
          {logoHtml}
          <p>Use the button below to set your password. Your username is:</p>
          <p style="font-size: 18px; font-weight: bold;">{WebUtility.HtmlEncode(username)}</p>
          {locationHtml}
          <p>
            <a href="{setPasswordUrl}" style="display: inline-block; padding: 12px 24px; background: {accentColorHex}; color: #fff; text-decoration: none; border-radius: 4px;">
              Set Your Password
            </a>
          </p>
          <p style="color: #666; font-size: 13px;">This link expires in 7 days. If it has expired, ask an administrator to send you a new one.</p>
        </div>
        """;
    }

    // --- Staff scheduling emails -------------------------------------------------------------

    private static string LogoHtml(string? logoUrl) => logoUrl is not null
        ? $"""<img src="{logoUrl}" alt="Company logo" style="max-height: 48px; display: block; margin-bottom: 12px;" />"""
        : string.Empty;

    private static string ButtonHtml(string url, string label, string accentColorHex) => $"""
        <p>
          <a href="{url}" style="display: inline-block; padding: 12px 24px; background: {accentColorHex}; color: #fff; text-decoration: none; border-radius: 4px;">
            {WebUtility.HtmlEncode(label)}
          </a>
        </p>
        """;

    private static string ShiftLinesHtml(string heading, IReadOnlyList<ShiftEmailLine> lines, bool struckThrough = false)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        string style = struckThrough ? " style=\"text-decoration: line-through; color: #666;\"" : string.Empty;
        string items = string.Concat(lines
            .OrderBy(x => x.Date)
            .Select(x => $"<li{style}>{WebUtility.HtmlEncode(ShiftLineText(x))}</li>"));

        return $"""<p style="margin-bottom: 4px;"><strong>{WebUtility.HtmlEncode(heading)}</strong></p><ul style="margin-top: 0;">{items}</ul>""";
    }

    // "Mon 5 Oct · 09:00–17:00 · Supervisor"
    private static string ShiftLineText(ShiftEmailLine line)
    {
        string text = $"{line.Date.ToString("ddd d MMM", RotaFormat.Uk)} · {line.TimeRange}";

        return line.PositionName is string position ? $"{text} · {position}" : text;
    }

    private static string BuildRotaChangedHtml(string greetingName, string locationName, IReadOnlyList<ShiftEmailLine> added, IReadOnlyList<ShiftEmailLine> changed, IReadOnlyList<ShiftEmailLine> removed, bool onlyNew, string myShiftsUrl, string? logoUrl, string accentColorHex)
    {
        string intro = onlyNew
            ? $"Your shifts at <strong>{WebUtility.HtmlEncode(locationName)}</strong> have been published."
            : $"Your rota at <strong>{WebUtility.HtmlEncode(locationName)}</strong> has changed.";

        return $"""
        <div style="font-family: Arial, sans-serif; max-width: 480px; margin: 0 auto;">
          {LogoHtml(logoUrl)}
          <h2>Lanyard</h2>
          <p>Hi {WebUtility.HtmlEncode(greetingName)},</p>
          <p>{intro}</p>
          {ShiftLinesHtml(onlyNew ? "Your shifts" : "New shifts", added)}
          {ShiftLinesHtml("Changed (new times shown)", changed)}
          {ShiftLinesHtml("Removed", removed, struckThrough: true)}
          {ButtonHtml(myShiftsUrl, "See my shifts", accentColorHex)}
          <p style="border-left: 4px solid {accentColorHex}; padding-left: 12px; color: #666; font-size: 13px;">
            If you can't work a shift, speak to your manager as soon as you can.
          </p>
        </div>
        """;
    }

    private static string BuildShiftReminderHtml(string greetingName, string locationName, ShiftEmailLine shift, string dayLabel, string myShiftsUrl, string? logoUrl, string accentColorHex)
    {
        string position = shift.PositionName is string name ? $" as <strong>{WebUtility.HtmlEncode(name)}</strong>" : string.Empty;

        return $"""
        <div style="font-family: Arial, sans-serif; max-width: 480px; margin: 0 auto;">
          {LogoHtml(logoUrl)}
          <h2>Lanyard</h2>
          <p>Hi {WebUtility.HtmlEncode(greetingName)},</p>
          <p>Just a reminder that you're working {WebUtility.HtmlEncode(dayLabel)} at
             <strong>{WebUtility.HtmlEncode(locationName)}</strong>{position}:
             <strong>{WebUtility.HtmlEncode(ShiftLineText(shift with { PositionName = null }))}</strong>.</p>
          {ButtonHtml(myShiftsUrl, "See my shifts", accentColorHex)}
        </div>
        """;
    }

    private static string BuildTimeOffRequestedHtml(string greetingName, string requesterName, string typeName, DateOnly start, DateOnly end, string amount, string? notes, string approvalsUrl, string? logoUrl, string accentColorHex)
    {
        string notesHtml = string.IsNullOrWhiteSpace(notes)
            ? string.Empty
            : $"""<p style="border-left: 4px solid {accentColorHex}; padding-left: 12px; color: #444;">“{WebUtility.HtmlEncode(notes)}”</p>""";

        return $"""
        <div style="font-family: Arial, sans-serif; max-width: 480px; margin: 0 auto;">
          {LogoHtml(logoUrl)}
          <h2>Lanyard</h2>
          <p>Hi {WebUtility.HtmlEncode(greetingName)},</p>
          <p><strong>{WebUtility.HtmlEncode(requesterName)}</strong> has asked for
             <strong>{WebUtility.HtmlEncode(typeName.ToLower())}</strong>:
             <strong>{WebUtility.HtmlEncode(TimeOffFormat.DateRange(start, end))}</strong> ({WebUtility.HtmlEncode(amount)}).</p>
          {notesHtml}
          {ButtonHtml(approvalsUrl, "Review the request", accentColorHex)}
        </div>
        """;
    }

    private static string BuildTimeOffDecisionHtml(string greetingName, string typeName, DateOnly start, DateOnly end, TimeOffEmailOutcome outcome, string? reason, string? decidedByName, string myTimeOffUrl, string? logoUrl, string accentColorHex)
    {
        string what = $"<strong>{WebUtility.HtmlEncode(typeName.ToLower())}</strong> for <strong>{WebUtility.HtmlEncode(TimeOffFormat.DateRange(start, end))}</strong>";
        string by = string.IsNullOrWhiteSpace(decidedByName) ? string.Empty : $" by {WebUtility.HtmlEncode(decidedByName)}";

        string message = outcome switch
        {
            TimeOffEmailOutcome.Approved => $"Your {what} has been approved{by}.",
            TimeOffEmailOutcome.Rejected => $"Your {what} wasn't approved{by}.",
            TimeOffEmailOutcome.Withdrawn => $"The approval for your {what} has been withdrawn{by}.",
            TimeOffEmailOutcome.CutShort => $"Your time off has been cut short{by}: {what} has been cancelled. The days before that still count as taken.",
            _ => $"Time off has been recorded for you{by}: {what}."
        };

        string reasonHtml = string.IsNullOrWhiteSpace(reason)
            ? string.Empty
            : $"""<p style="border-left: 4px solid {accentColorHex}; padding-left: 12px; color: #444;">“{WebUtility.HtmlEncode(reason)}”</p>""";

        return $"""
        <div style="font-family: Arial, sans-serif; max-width: 480px; margin: 0 auto;">
          {LogoHtml(logoUrl)}
          <h2>Lanyard</h2>
          <p>Hi {WebUtility.HtmlEncode(greetingName)},</p>
          <p>{message}</p>
          {reasonHtml}
          {ButtonHtml(myTimeOffUrl, "See my time off", accentColorHex)}
        </div>
        """;
    }
}

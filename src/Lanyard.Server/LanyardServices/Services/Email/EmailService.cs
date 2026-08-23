using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lanyard.Infrastructure.DTO;
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
        try
        {
            EmailOptions config = _options.Value;

            if (string.IsNullOrWhiteSpace(config.ResendApiKey) || string.IsNullOrWhiteSpace(config.FromAddress))
            {
                return Result<bool>.Fail("Email is not configured (missing Resend API key or From address).");
            }

            if (string.IsNullOrWhiteSpace(user.Email))
            {
                return Result<bool>.Fail("User has no email address to send a link to.");
            }

            string html = BuildSetPasswordHtml(user.UserName ?? user.Email, setPasswordUrl, logoUrl, accentColorHex, locationName);

            HttpRequestMessage request = new(HttpMethod.Post, "emails")
            {
                Content = JsonContent.Create(new
                {
                    from = $"{config.FromName} <{config.FromAddress}>",
                    to = new[] { user.Email },
                    subject = "Set your Lanyard password",
                    html
                })
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
            _logger.LogError(ex, "Exception sending set-password email to {UserId}", user.Id);
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> SendCourseRecurrenceReminderEmailAsync(UserProfile user, string courseName, string trainingUrl, string? logoUrl, string accentColorHex)
    {
        try
        {
            EmailOptions config = _options.Value;

            if (string.IsNullOrWhiteSpace(config.ResendApiKey) || string.IsNullOrWhiteSpace(config.FromAddress))
            {
                return Result<bool>.Fail("Email is not configured (missing Resend API key or From address).");
            }

            if (string.IsNullOrWhiteSpace(user.Email))
            {
                return Result<bool>.Fail("User has no email address to send a link to.");
            }

            string html = BuildRecurrenceReminderHtml(user.UserName ?? user.Email, courseName, trainingUrl, logoUrl, accentColorHex);

            HttpRequestMessage request = new(HttpMethod.Post, "emails")
            {
                Content = JsonContent.Create(new
                {
                    from = $"{config.FromName} <{config.FromAddress}>",
                    to = new[] { user.Email },
                    subject = $"Time to retake: {courseName}",
                    html
                })
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
            _logger.LogError(ex, "Exception sending course recurrence reminder email to {UserId}", user.Id);
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> SendTwoFactorCodeEmailAsync(UserProfile user, string code)
    {
        try
        {
            EmailOptions config = _options.Value;

            if (string.IsNullOrWhiteSpace(config.ResendApiKey) || string.IsNullOrWhiteSpace(config.FromAddress))
            {
                return Result<bool>.Fail("Email is not configured (missing Resend API key or From address).");
            }

            if (string.IsNullOrWhiteSpace(user.Email))
            {
                return Result<bool>.Fail("User has no email address to send a code to.");
            }

            string html = BuildTwoFactorCodeHtml(code);

            HttpRequestMessage request = new(HttpMethod.Post, "emails")
            {
                Content = JsonContent.Create(new
                {
                    from = $"{config.FromName} <{config.FromAddress}>",
                    to = new[] { user.Email },
                    subject = "Your Lanyard sign-in code",
                    html
                })
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
            _logger.LogError(ex, "Exception sending two-factor code email to {UserId}", user.Id);
            return Result<bool>.Fail(ex.Message);
        }
    }

    private static string BuildTwoFactorCodeHtml(string code)
    {
        return $"""
        <div style="font-family: Arial, sans-serif; max-width: 480px; margin: 0 auto;">
          <h2>Lanyard</h2>
          <p>Your sign-in code is:</p>
          <p style="font-size: 28px; font-weight: bold; letter-spacing: 4px;">{WebUtility.HtmlEncode(code)}</p>
          <p style="color: #666; font-size: 13px;">This code expires shortly. If you didn't try to sign in, you can ignore this email.</p>
        </div>
        """;
    }

    private static string BuildRecurrenceReminderHtml(string username, string courseName, string trainingUrl, string? logoUrl, string accentColorHex)
    {
        string logoHtml = logoUrl is not null
            ? $"""<img src="{logoUrl}" alt="Company logo" style="max-height: 48px; display: block; margin-bottom: 12px;" />"""
            : string.Empty;

        return $"""
        <div style="font-family: Arial, sans-serif; max-width: 480px; margin: 0 auto;">
          {logoHtml}
          <h2>Lanyard</h2>
          <p>Hi {WebUtility.HtmlEncode(username)},</p>
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

    private static string BuildSetPasswordHtml(string username, string setPasswordUrl, string? logoUrl, string accentColorHex, string? locationName)
    {
        string logoHtml = logoUrl is not null
            ? $"""<img src="{logoUrl}" alt="Company logo" style="max-height: 48px; display: block; margin-bottom: 12px;" />"""
            : string.Empty;

        string locationHtml = locationName is not null
            ? $"""<p>Log in at: <strong>{WebUtility.HtmlEncode(locationName)}</strong></p>"""
            : string.Empty;

        return $"""
        <div style="font-family: Arial, sans-serif; max-width: 480px; margin: 0 auto;">
          {logoHtml}
          <h2>Lanyard</h2>
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
}

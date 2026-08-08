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

    public async Task<Result<bool>> SendSetPasswordEmailAsync(UserProfile user, string setPasswordUrl)
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

            string html = BuildSetPasswordHtml(user.UserName ?? user.Email, setPasswordUrl);

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

    private static string BuildSetPasswordHtml(string username, string setPasswordUrl)
    {
        return $"""
        <div style="font-family: Arial, sans-serif; max-width: 480px; margin: 0 auto;">
          <h2>Lanyard</h2>
          <p>Use the button below to set your password. Your username is:</p>
          <p style="font-size: 18px; font-weight: bold;">{WebUtility.HtmlEncode(username)}</p>
          <p>
            <a href="{setPasswordUrl}" style="display: inline-block; padding: 12px 24px; background: #0F6CBD; color: #fff; text-decoration: none; border-radius: 4px;">
              Set Your Password
            </a>
          </p>
          <p style="color: #666; font-size: 13px;">This link expires in 7 days. If it has expired, ask an administrator to send you a new one.</p>
        </div>
        """;
    }
}

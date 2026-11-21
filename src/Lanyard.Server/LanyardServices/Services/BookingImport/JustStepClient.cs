using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.BookingImport;

// Signs in to Just Step Online and reads its booking report by driving the same requests a browser
// makes: the Laravel login form (CSRF token + session cookie), then the report's AJAX endpoint.
//
// Cookies and redirects are handled by hand (the named client turns both off) so each call is a
// self-contained session and one location's sign-in can't leak into another's.
public sealed partial class JustStepClient(IHttpClientFactory httpClientFactory, ILogger<JustStepClient> logger) : IJustStepClient
{
    public const string HttpClientName = "juststep";

    private const string BaseUrl = "https://juststeponline.com";
    private const string LoginPath = "/admin/login";
    private const string ReportPagePath = "/admin/booking-reports";
    private const string ReportDataPath = "/admin/booking-report-list";

    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(45);

    public async Task<Result<bool>> TestLoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OverallTimeout);

        try
        {
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            Dictionary<string, string> cookies = [];
            Result<bool> login = await SignInAsync(client, cookies, username, password, timeout.Token);
            return login.IsSuccess ? Result<bool>.Ok(true) : Result<bool>.Fail(login.Error!);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<bool>.Fail("Just Step Online took too long to answer.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation("Just Step test login failed: {Error}", ex.Message);
            return Result<bool>.Fail("Couldn't reach Just Step Online. Check the connection and try again.");
        }
    }

    public async Task<Result<IReadOnlyList<ExternalBookingDto>>> FetchBookingsAsync(
        string username, string password, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OverallTimeout);

        try
        {
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            Dictionary<string, string> cookies = [];

            Result<bool> login = await SignInAsync(client, cookies, username, password, timeout.Token);
            if (!login.IsSuccess)
            {
                return Result<IReadOnlyList<ExternalBookingDto>>.Fail(login.Error!);
            }

            // The report page carries the CSRF token the AJAX endpoint checks.
            (string reportPage, _) = await SendAsync(client, HttpMethod.Get, ReportPagePath, cookies, null, null, timeout.Token);
            string? csrf = ExtractCsrfToken(reportPage);

            if (csrf is null)
            {
                return Result<IReadOnlyList<ExternalBookingDto>>.Fail("Signed in, but Just Step Online's report page looked different than expected.");
            }

            Dictionary<string, string> form = new()
            {
                ["fromdate"] = from.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
                ["todate"] = to.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
                ["report_type"] = "2",
                ["is_report"] = "2",
                ["report_by"] = "-1"
            };

            Dictionary<string, string> headers = new()
            {
                ["X-CSRF-TOKEN"] = csrf,
                ["X-Requested-With"] = "XMLHttpRequest"
            };

            (string reportHtml, HttpStatusCode status) = await SendAsync(client, HttpMethod.Post, ReportDataPath, cookies, form, headers, timeout.Token);

            if (status != HttpStatusCode.OK || string.IsNullOrWhiteSpace(reportHtml))
            {
                return Result<IReadOnlyList<ExternalBookingDto>>.Fail($"Just Step Online's booking report returned {(int)status}.");
            }

            IReadOnlyList<ExternalBookingDto> bookings = JustStepReportParser.Parse(reportHtml);
            return Result<IReadOnlyList<ExternalBookingDto>>.Ok(bookings);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<IReadOnlyList<ExternalBookingDto>>.Fail("Just Step Online took too long to answer.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation("Just Step booking fetch failed: {Error}", ex.Message);
            return Result<IReadOnlyList<ExternalBookingDto>>.Fail("Couldn't reach Just Step Online. Check the connection and try again.");
        }
    }

    // Gets the login form (for its CSRF field + cookies), posts the credentials, and checks the
    // redirect: success goes to /admin, a bad login bounces back to /admin/login.
    private async Task<Result<bool>> SignInAsync(HttpClient client, Dictionary<string, string> cookies, string username, string password, CancellationToken ct)
    {
        (string loginPage, HttpStatusCode pageStatus) = await SendAsync(client, HttpMethod.Get, LoginPath, cookies, null, null, ct);

        if (pageStatus is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            return Result<bool>.Fail("Just Step Online is blocking automated sign-in right now. Try again later.");
        }

        string? formToken = ExtractFormToken(loginPage);
        if (formToken is null)
        {
            return Result<bool>.Fail("Couldn't read Just Step Online's sign-in page.");
        }

        Dictionary<string, string> form = new()
        {
            ["_token"] = formToken,
            ["email"] = username,
            ["password"] = password
        };

        using HttpRequestMessage request = BuildRequest(HttpMethod.Post, LoginPath, cookies, form, null);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        CaptureCookies(response, cookies);

        if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.Redirect)
        {
            string location = response.Headers.Location?.ToString() ?? string.Empty;

            // Laravel bounces a failed login back to the login page; success goes to the dashboard.
            return location.Contains("/login", StringComparison.OrdinalIgnoreCase)
                ? Result<bool>.Fail("Just Step Online rejected those sign-in details.")
                : Result<bool>.Ok(true);
        }

        // No redirect means the form came back with validation errors, i.e. a bad login.
        return Result<bool>.Fail("Just Step Online rejected those sign-in details.");
    }

    private async Task<(string Body, HttpStatusCode Status)> SendAsync(
        HttpClient client, HttpMethod method, string path, Dictionary<string, string> cookies,
        Dictionary<string, string>? form, Dictionary<string, string>? headers, CancellationToken ct)
    {
        using HttpRequestMessage request = BuildRequest(method, path, cookies, form, headers);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        CaptureCookies(response, cookies);

        string body = await ReadLimitedAsync(response, ct);
        return (body, response.StatusCode);
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string path, Dictionary<string, string> cookies,
        Dictionary<string, string>? form, Dictionary<string, string>? headers)
    {
        HttpRequestMessage request = new(method, BaseUrl + path);

        if (cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}")));
        }

        if (headers is not null)
        {
            foreach ((string name, string value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        return request;
    }

    // Merges Set-Cookie values into the running cookie jar (name=value, dropping attributes).
    private static void CaptureCookies(HttpResponseMessage response, Dictionary<string, string> cookies)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies))
        {
            return;
        }

        foreach (string raw in setCookies)
        {
            string pair = raw.Split(';', 2)[0].Trim();
            int eq = pair.IndexOf('=');

            if (eq > 0)
            {
                cookies[pair[..eq]] = pair[(eq + 1)..];
            }
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[32 * 1024];
        int read;

        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            int take = Math.Min(read, MaxResponseBytes - (int)buffer.Length);
            buffer.Write(chunk, 0, take);

            if (buffer.Length >= MaxResponseBytes)
            {
                break;
            }
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string? ExtractFormToken(string html)
    {
        Match match = FormToken().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractCsrfToken(string html)
    {
        Match jsVar = CsrfJsVar().Match(html);
        if (jsVar.Success)
        {
            return jsVar.Groups[1].Value;
        }

        // Fall back to the meta tag some pages carry, or the form field.
        Match meta = CsrfMeta().Match(html);
        if (meta.Success)
        {
            return meta.Groups[1].Value;
        }

        return ExtractFormToken(html);
    }

    [GeneratedRegex(@"name=""_token""[^>]*value=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex FormToken();

    [GeneratedRegex(@"TOKEN\s*=\s*['""]([^'""]+)['""]")]
    private static partial Regex CsrfJsVar();

    [GeneratedRegex(@"<meta\s+name=""csrf-token""\s+content=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CsrfMeta();
}

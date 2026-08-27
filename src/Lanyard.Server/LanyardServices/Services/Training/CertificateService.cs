using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.Branding;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Lanyard.Application.Services.Training;

public class CertificateService(
    IDbContextFactory<ApplicationDbContext> factory,
    ICompanyLocationService companyLocationService,
    IFileService fileService,
    ILogger<CertificateService> logger) : ICertificateService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ICompanyLocationService _companyLocationService = companyLocationService;
    private readonly IFileService _fileService = fileService;
    private readonly ILogger<CertificateService> _logger = logger;

    // Set here rather than in Program.cs so the unit tests exercise the real renderer
    // without booting the web host - QuestPDF throws on first use if this is unset.
    // Community licence: free for organisations under $1M USD annual revenue. If that
    // stops being true, this needs replacing with a paid Professional/Enterprise key.
    static CertificateService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public async Task<Result<byte[]>> GenerateCertificatePdfAsync(Guid assignmentId, string requestingUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            CourseAssignment? assignment = await ctx.CourseAssignments
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Course)
                .FirstOrDefaultAsync(x => x.Id == assignmentId && x.IsActive, cancellationToken);

            if (assignment is null || assignment.Course is null)
            {
                return Result<byte[]>.Fail("Assignment not found.");
            }

            if (assignment.UserId != requestingUserId)
            {
                return Result<byte[]>.Fail("You do not have access to this assignment.");
            }

            if (assignment.CompletedDate is not DateTime completedDate)
            {
                return Result<byte[]>.Fail("This course has not been completed yet.");
            }

            UserProfile? user = await ctx.Users
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == assignment.UserId, cancellationToken);

            if (user is null)
            {
                return Result<byte[]>.Fail("User not found.");
            }

            string recipientName = ResolveRecipientName(user);
            (byte[]? logoBytes, string accentColorHex) = await ResolveBrandingAsync(assignment, cancellationToken);

            byte[] pdf = Render(recipientName, assignment.Course.Name, assignment.Course.PassMarkPercent, completedDate, logoBytes, accentColorHex);

            return Result<byte[]>.Ok(pdf);
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Fail($"Failed to generate certificate: {ex.Message}");
        }
    }

    private static string ResolveRecipientName(UserProfile user)
    {
        string name = user.GetName().Trim();

        return string.IsNullOrWhiteSpace(name) ? user.UserName ?? "Unknown" : name;
    }

    // Same branding lookup as CourseAssignmentService's assigned-email path, but resolving
    // the logo to bytes instead of a URL - a PDF has to carry the image itself so it still
    // renders when printed or opened offline. A logo that can't be loaded is dropped rather
    // than failing the certificate: the rest of the document is still valid without it.
    private async Task<(byte[]? LogoBytes, string AccentColorHex)> ResolveBrandingAsync(CourseAssignment assignment, CancellationToken cancellationToken)
    {
        string accentColorHex = BrandConstants.PrimaryColorHex;

        if (assignment.LocationId is not int locationId)
        {
            return (null, accentColorHex);
        }

        try
        {
            Result<CompanyBrandingInfo> brandingResult = await _companyLocationService.GetCompanyBrandingForLocationAsync(locationId);

            if (!brandingResult.IsSuccess || brandingResult.Data is null)
            {
                return (null, accentColorHex);
            }

            accentColorHex = BrandConstants.ResolveAccentColor(brandingResult.Data.ThemeColorHex);

            if (brandingResult.Data.LogoFileId is not Guid logoFileId)
            {
                return (null, accentColorHex);
            }

            Result<Stream> logoResult = await _fileService.DownloadFileAsync(logoFileId, cancellationToken);

            if (!logoResult.IsSuccess || logoResult.Data is null)
            {
                _logger.LogWarning("Could not load logo {LogoFileId} for certificate on assignment {AssignmentId}: {Error}",
                    logoFileId, assignment.Id, logoResult.Error);

                return (null, accentColorHex);
            }

            await using Stream logoStream = logoResult.Data;
            using MemoryStream buffer = new();
            await logoStream.CopyToAsync(buffer, cancellationToken);

            return (buffer.ToArray(), accentColorHex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve branding for certificate on assignment {AssignmentId}", assignment.Id);

            return (null, accentColorHex);
        }
    }

    private static byte[] Render(string recipientName, string courseName, int passMarkPercent, DateTime completedDate, byte[]? logoBytes, string accentColorHex)
    {
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(50);
                page.DefaultTextStyle(x => x.FontSize(14).FontColor(Colors.Grey.Darken4));

                page.Content().Border(2).BorderColor(accentColorHex).Padding(30).Column(column =>
                {
                    column.Spacing(18);

                    if (logoBytes is not null)
                    {
                        column.Item().AlignCenter().Height(60).Image(logoBytes).FitHeight();
                    }

                    column.Item().AlignCenter().Text("Certificate of Completion")
                        .FontSize(30).Bold().FontColor(accentColorHex);

                    column.Item().AlignCenter().Text("This is to certify that").FontSize(13).FontColor(Colors.Grey.Darken1);

                    column.Item().AlignCenter().Text(recipientName).FontSize(26).Bold();

                    column.Item().AlignCenter().Text("has successfully completed the training course").FontSize(13).FontColor(Colors.Grey.Darken1);

                    column.Item().AlignCenter().Text(courseName).FontSize(20).SemiBold().FontColor(accentColorHex);

                    column.Item().PaddingTop(10).AlignCenter().Text($"Completed on {completedDate:d MMMM yyyy}").FontSize(13);

                    column.Item().AlignCenter().Text($"Achieving the required pass mark of {passMarkPercent}%")
                        .FontSize(11).FontColor(Colors.Grey.Darken1);
                });

                page.Footer().AlignCenter().Text("Issued by Lanyard").FontSize(9).FontColor(Colors.Grey.Medium);
            });
        }).GeneratePdf();
    }
}

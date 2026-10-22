using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.Branding;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Parties;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Parties;

public class PartyDocumentService(
    IPartyBookingService bookingService,
    IPartySettingsService settingsService,
    IPartyTemplateService templateService,
    ICompanyLocationService companyLocationService,
    IFileService fileService,
    ILogger<PartyDocumentService> logger) : IPartyDocumentService
{
    private readonly IPartyBookingService _bookingService = bookingService;
    private readonly IPartySettingsService _settingsService = settingsService;
    private readonly IPartyTemplateService _templateService = templateService;
    private readonly ICompanyLocationService _companyLocationService = companyLocationService;
    private readonly IFileService _fileService = fileService;
    private readonly ILogger<PartyDocumentService> _logger = logger;

    public async Task<Result<PartyDocument>> BuildAsync(LocationScope scope, Guid bookingId, PartyDocumentType type, string? authorisedBy, CancellationToken cancellationToken = default)
    {
        try
        {
            Result<PartyBooking> bookingResult = await _bookingService.GetBookingAsync(scope, bookingId);

            if (!bookingResult.IsSuccess || bookingResult.Data is null)
            {
                return Result<PartyDocument>.Fail(bookingResult.Error ?? "Party not found.");
            }

            PartyBooking booking = bookingResult.Data;

            Result<List<PartyMenuItem>> menuResult = await _settingsService.GetMenuAsync(scope, booking.LocationId);

            if (!menuResult.IsSuccess || menuResult.Data is null)
            {
                return Result<PartyDocument>.Fail(menuResult.Error ?? "Couldn't load the party menu.");
            }

            (byte[]? logo, string accent, string? companyName) = await ResolveBrandingAsync(booking.LocationId, cancellationToken);

            bool needsVoucher = type is PartyDocumentType.Voucher or PartyDocumentType.Pack;
            bool needsFeedback = type is PartyDocumentType.FeedbackForm or PartyDocumentType.Pack;

            PartyDocumentModel model = new(
                booking,
                booking.PartyHost?.GetName(),
                menuResult.Data,
                logo,
                accent,
                needsVoucher ? await LoadArtworkAsync(scope, booking.CompanyId, PartyArtworkDocument.Voucher, cancellationToken) : null,
                needsFeedback ? await LoadArtworkAsync(scope, booking.CompanyId, PartyArtworkDocument.FeedbackForm, cancellationToken) : null,
                authorisedBy,
                companyName);

            byte[] pdf = PartyDocumentRenderer.Render(model, type);

            return Result<PartyDocument>.Ok(new PartyDocument(pdf, FileName(booking, type)));
        }
        catch (Exception ex)
        {
            return Result<PartyDocument>.Fail($"Failed to create the party document: {ex.Message}");
        }
    }

    public async Task<Result<PartyDocument>> BuildPreviewAsync(LocationScope scope, int companyId, PartyArtworkDocument document, string? authorisedBy, CancellationToken cancellationToken = default)
    {
        try
        {
            Result<PartyDocumentTemplate> template = await _templateService.GetTemplateAsync(scope, companyId, document);

            if (!template.IsSuccess || template.Data is null)
            {
                return Result<PartyDocument>.Fail(template.Error ?? "Couldn't load the printout.");
            }

            PartyArtwork? artwork = await LoadArtworkAsync(scope, companyId, document, cancellationToken);
            Result<CompanyBrandingInfo> branding = await _companyLocationService.GetCompanyBrandingAsync(companyId);
            byte[]? logo = await DownloadAsync(branding.Data?.LogoFileId, cancellationToken);

            DateOnly sampleDay = RotaTime.Today(DateTime.UtcNow).AddDays(7);

            PartyBooking sample = new()
            {
                CompanyId = companyId,
                LocationId = 0,
                ChildName = "Florence-May",
                ChildAgeTurning = 7,
                StartUtc = RotaTime.ToUtc(sampleDay, new TimeOnly(11, 0)),
                EndUtc = RotaTime.ToUtc(sampleDay, new TimeOnly(13, 0)),
                ExpectedChildren = 12
            };

            PartyDocumentType type = document == PartyArtworkDocument.Voucher ? PartyDocumentType.Voucher : PartyDocumentType.FeedbackForm;

            PartyDocumentModel model = new(
                sample,
                "Sam Okafor",
                [],
                logo,
                BrandConstants.ResolveAccentColor(branding.Data?.ThemeColorHex),
                document == PartyArtworkDocument.Voucher ? artwork : null,
                document == PartyArtworkDocument.FeedbackForm ? artwork : null,
                authorisedBy,
                branding.Data?.Name);

            return Result<PartyDocument>.Ok(new PartyDocument(PartyDocumentRenderer.Render(model, type), $"{type} preview.pdf"));
        }
        catch (Exception ex)
        {
            return Result<PartyDocument>.Fail($"Failed to create the preview: {ex.Message}");
        }
    }

    private static string FileName(PartyBooking booking, PartyDocumentType type)
    {
        string what = type switch
        {
            PartyDocumentType.ReservedCard => "reserved card",
            PartyDocumentType.FoodSheet => booking.MenuType == PartyMenuType.Cold ? "cold food sheet" : "hot food sheet",
            PartyDocumentType.Voucher => "voucher",
            PartyDocumentType.FeedbackForm => "feedback form",
            _ => "party pack"
        };

        string child = string.Concat(booking.ChildName.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != '"'));

        return $"{child} {RotaTime.LocalDate(booking.StartUtc):yyyy-MM-dd} {what}.pdf";
    }

    // The company's template and artwork image. With no template, or artwork that can't be loaded,
    // the standard design prints instead - a missing image shouldn't stop the party's printouts.
    private async Task<PartyArtwork?> LoadArtworkAsync(LocationScope scope, int companyId, PartyArtworkDocument document, CancellationToken cancellationToken)
    {
        Result<PartyDocumentTemplate> template = await _templateService.GetTemplateAsync(scope, companyId, document);

        if (!template.IsSuccess || template.Data is null)
        {
            _logger.LogWarning("Could not load the {Document} template for company {CompanyId}: {Error}", document, companyId, template.Error);
            return null;
        }

        byte[]? image = await DownloadAsync(template.Data.ArtworkFileId, cancellationToken);

        return new PartyArtwork(template.Data, PartyTemplateFields.Parse(template.Data.FieldsJson), image);
    }

    // The company's logo and colour, like the training certificate. A logo that can't be loaded
    // is left off rather than failing the printout - the sheet is still usable without it.
    private async Task<(byte[]? Logo, string AccentColorHex, string? CompanyName)> ResolveBrandingAsync(int locationId, CancellationToken cancellationToken)
    {
        Result<CompanyBrandingInfo> branding = await _companyLocationService.GetCompanyBrandingForLocationAsync(locationId);

        return (await DownloadAsync(branding.Data?.LogoFileId, cancellationToken),
            BrandConstants.ResolveAccentColor(branding.Data?.ThemeColorHex),
            branding.Data?.Name);
    }

    private async Task<byte[]?> DownloadAsync(Guid? fileId, CancellationToken cancellationToken)
    {
        if (fileId is not Guid id)
        {
            return null;
        }

        try
        {
            Result<Stream> file = await _fileService.DownloadFileAsync(id, cancellationToken);

            if (!file.IsSuccess || file.Data is null)
            {
                _logger.LogWarning("Could not load image {FileId} for a party document: {Error}", id, file.Error);
                return null;
            }

            await using Stream stream = file.Data;
            using MemoryStream buffer = new();
            await stream.CopyToAsync(buffer, cancellationToken);

            return buffer.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load image {FileId} for a party document", id);
            return null;
        }
    }
}

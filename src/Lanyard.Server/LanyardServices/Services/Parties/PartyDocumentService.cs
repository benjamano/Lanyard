using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.Branding;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Parties;

public class PartyDocumentService(
    IPartyBookingService bookingService,
    IPartySettingsService settingsService,
    ICompanyLocationService companyLocationService,
    IFileService fileService,
    ILogger<PartyDocumentService> logger) : IPartyDocumentService
{
    private readonly IPartyBookingService _bookingService = bookingService;
    private readonly IPartySettingsService _settingsService = settingsService;
    private readonly ICompanyLocationService _companyLocationService = companyLocationService;
    private readonly IFileService _fileService = fileService;
    private readonly ILogger<PartyDocumentService> _logger = logger;

    public async Task<Result<PartyDocument>> BuildAsync(LocationScope scope, Guid bookingId, PartyDocumentType type, CancellationToken cancellationToken = default)
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

            (byte[]? logo, string accent) = await ResolveBrandingAsync(booking.LocationId, cancellationToken);

            PartyDocumentModel model = new(booking, booking.PartyHost?.GetName(), menuResult.Data, logo, accent);

            byte[] pdf = PartyDocumentRenderer.Render(model, type);

            return Result<PartyDocument>.Ok(new PartyDocument(pdf, FileName(booking, type)));
        }
        catch (Exception ex)
        {
            return Result<PartyDocument>.Fail($"Failed to create the party document: {ex.Message}");
        }
    }

    private static string FileName(PartyBooking booking, PartyDocumentType type)
    {
        string what = type switch
        {
            PartyDocumentType.ReservedCard => "reserved card",
            PartyDocumentType.FoodSheet => booking.MenuType == PartyMenuType.Cold ? "cold food sheet" : "hot food sheet",
            _ => "party pack"
        };

        string child = string.Concat(booking.ChildName.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != '"'));

        return $"{child} {RotaTime.LocalDate(booking.StartUtc):yyyy-MM-dd} {what}.pdf";
    }

    // The company's logo and colour, like the training certificate. A logo that can't be loaded
    // is left off rather than failing the printout - the sheet is still usable without it.
    private async Task<(byte[]? Logo, string AccentColorHex)> ResolveBrandingAsync(int locationId, CancellationToken cancellationToken)
    {
        Result<CompanyBrandingInfo> branding = await _companyLocationService.GetCompanyBrandingForLocationAsync(locationId);

        string accent = BrandConstants.ResolveAccentColor(branding.Data?.ThemeColorHex);

        if (branding.Data?.LogoFileId is not Guid logoFileId)
        {
            return (null, accent);
        }

        try
        {
            Result<Stream> logo = await _fileService.DownloadFileAsync(logoFileId, cancellationToken);

            if (!logo.IsSuccess || logo.Data is null)
            {
                _logger.LogWarning("Could not load logo {LogoFileId} for a party document at location {LocationId}: {Error}",
                    logoFileId, locationId, logo.Error);

                return (null, accent);
            }

            await using Stream stream = logo.Data;
            using MemoryStream buffer = new();
            await stream.CopyToAsync(buffer, cancellationToken);

            return (buffer.ToArray(), accent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the logo for a party document at location {LocationId}", locationId);

            return (null, accent);
        }
    }
}

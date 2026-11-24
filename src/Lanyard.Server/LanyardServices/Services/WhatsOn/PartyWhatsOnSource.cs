using Lanyard.Application.Services.Features;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Parties;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.DTO.WhatsOn;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.WhatsOn;

// Party bookings (hand-entered and imported) as What's on entries.
public class PartyWhatsOnSource(IPartyBookingService partyBookingService, ICompanyFeatureService featureService) : IWhatsOnSource
{
    private readonly IPartyBookingService _partyBookingService = partyBookingService;
    private readonly ICompanyFeatureService _featureService = featureService;

    public async Task<Result<List<WhatsOnItem>>> GetItemsAsync(LocationScope scope, int companyId, int locationId, DateOnly from, DateOnly to, bool includeCancelled)
    {
        if (!await _featureService.IsEnabledForLocationAsync(locationId, CompanyFeature.Parties))
        {
            return Result<List<WhatsOnItem>>.Ok([]);
        }

        Result<List<PartyBooking>> bookings = await _partyBookingService.GetBookingsAsync(scope, locationId, from, to, includeCancelled);

        if (!bookings.IsSuccess || bookings.Data is null)
        {
            return Result<List<WhatsOnItem>>.Fail($"Parties couldn't be loaded: {bookings.Error}");
        }

        return Result<List<WhatsOnItem>>.Ok(bookings.Data.Select(ToItem).ToList());
    }

    public static WhatsOnItem ToItem(PartyBooking party)
    {
        List<WhatsOnMarker> markers = [];

        if (party.EatTimeUtc is DateTime eat)
        {
            markers.Add(new WhatsOnMarker("Eat", eat));
        }

        if (party.LaserTagTimeUtc is DateTime laser)
        {
            markers.Add(new WhatsOnMarker("Laser", laser));
        }

        string title = party.IsPrivateHire
            ? string.IsNullOrWhiteSpace(party.ContactName) ? "Private hire" : $"Private hire · {party.ContactName}"
            : string.IsNullOrWhiteSpace(party.ChildName) ? "Party"
            : party.ChildAgeTurning is int age ? $"{party.ChildName}, turning {age}" : party.ChildName;

        string? subtitle = party.HireKind is PrivateHireKind kind
            ? kind.Label()
            : string.IsNullOrWhiteSpace(party.PartyType) ? null : party.PartyType;

        return new WhatsOnItem
        {
            Kind = WhatsOnKind.Party,
            Id = party.Id,
            Title = title,
            Subtitle = subtitle,
            Href = $"/manage/parties/{party.Id}",
            StartUtc = party.StartUtc,
            EndUtc = party.EndUtc,
            Markers = markers,
            Children = party.ExpectedChildren,
            Adults = party.ExpectedAdults,
            CountsGuests = party.IsPrivateHire,
            Room = string.IsNullOrWhiteSpace(party.Room) ? null : party.Room,
            HostName = party.PartyHost is null ? null : RotaNames.For(party.PartyHost),
            AllergyNotes = string.IsNullOrWhiteSpace(party.AllergyNotes) ? null : party.AllergyNotes,
            IsProvisional = party.ExternalIsProvisional,
            IsCancelled = party.Status == PartyBookingStatus.Cancelled,
            PaymentStatus = party.PaymentStatus,
            BalanceDue = party.BalanceDue,
            // A private hire has no party food.
            MenuType = party.IsPrivateHire ? null : party.MenuType
        };
    }
}

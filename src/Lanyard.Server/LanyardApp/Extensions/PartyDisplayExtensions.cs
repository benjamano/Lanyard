using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Lanyard.App.Extensions;

// Venue-local formatting for party bookings, through RotaTime like the rota (the server runs in UTC).
public static class PartyDisplayExtensions
{
    public static DateOnly LocalDate(this PartyBooking booking) => RotaTime.LocalDate(booking.StartUtc);

    public static string TimeRange(this PartyBooking booking) => RotaFormat.TimeRange(booking.StartUtc, booking.EndUtc);

    // "Lily, turning 7" / "Lily".
    public static string ChildLabel(this PartyBooking booking) =>
        booking.ChildAgeTurning is int age ? $"{booking.ChildName}, turning {age}" : booking.ChildName;

    public static string Money(decimal amount) => amount.ToString("C", RotaFormat.Uk);

    public static string Label(this PartyPaymentStatus status) => status switch
    {
        PartyPaymentStatus.PaidInFull => "Paid in full",
        PartyPaymentStatus.DepositPaid => "Deposit paid",
        _ => "Unpaid"
    };

    public static BadgeColor Color(this PartyPaymentStatus status) => status switch
    {
        PartyPaymentStatus.PaidInFull => BadgeColor.Success,
        PartyPaymentStatus.DepositPaid => BadgeColor.Brand,
        _ => BadgeColor.Warning
    };

    public static string Label(this PartyMenuType menu) => menu == PartyMenuType.Cold ? "Cold food" : "Hot food";
}

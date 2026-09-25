using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.Models
{
    // A kind of absence a company recognises: "Paid holiday", "Unpaid leave", "Sickness"...
    // Company-scoped like StaffPosition, because leave policy is set by the company, not a site.
    // Whether a type eats into an allowance is a property of the type: sickness is recorded but
    // never counted against anyone's holiday.
    public class TimeOffType
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public required string Name { get; set; }
        public string? Description { get; set; }

        // Informational for payroll; Lanyard doesn't calculate pay.
        public bool IsPaid { get; set; }

        // True: requests of this type count against the person's allowance for the holiday year.
        public bool DeductsFromAllowance { get; set; } = true;

        public int SortOrder { get; set; }

        public bool IsActive { get; set; } = true;
    }

    // How many hours of one time-off type a person gets per holiday year. Tiered like
    // ContractRequirement, keyed by which of StaffPositionId / UserId is set:
    //   - both null           -> the company default for this type
    //   - StaffPositionId set -> override for everyone whose primary position this is
    //   - UserId set          -> override for one person
    // Unlike a contract, an allowance is a single value, so a row either exists at a tier (and
    // wins) or doesn't (and the tier below applies). No row at any tier means no allowance: 0 h.
    // Stored in hours so half days and short absences deduct exactly; the UI converts to days
    // with CompanySchedulingSettings.HoursPerDay.
    public class TimeOffAllowance
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public required Guid TimeOffTypeId { get; set; }
        public TimeOffType? TimeOffType { get; set; }

        public Guid? StaffPositionId { get; set; }
        public StaffPosition? StaffPosition { get; set; }

        public string? UserId { get; set; }
        public UserProfile? User { get; set; }

        // e.g. unpaid leave for Customer Service Advisors. AllowanceHours is ignored when set.
        public bool IsUnlimited { get; set; }

        public decimal AllowanceHours { get; set; }

        public DateTime UpdateDate { get; set; }
        public string? UpdatedByUserId { get; set; }
    }

    // One request for a stretch of whole days off. Hours is what it costs against the allowance:
    // it defaults to days x HoursPerDay but is editable, because a part-timer's "day" or a half day
    // isn't the company's standard day. A request never spans two holiday years - the service
    // refuses it and asks for two - so each request belongs to exactly one year's balance.
    public class TimeOffRequest
    {
        public Guid Id { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        public required Guid TimeOffTypeId { get; set; }
        public TimeOffType? TimeOffType { get; set; }

        // The location the person asked from; that location's managers decide it.
        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        public DateOnly StartDate { get; set; }
        public DateOnly EndDate { get; set; }

        public decimal Hours { get; set; }

        public string? Notes { get; set; }

        public TimeOffStatus Status { get; set; } = TimeOffStatus.Pending;

        public DateTime RequestedDateUtc { get; set; }

        // The person themselves, or the manager who recorded it on their behalf (sickness, say).
        public string? RequestedByUserId { get; set; }

        public string? DecidedByUserId { get; set; }
        public DateTime? DecidedDateUtc { get; set; }
        public string? DecisionReason { get; set; }

        public DateTime? CancelledDateUtc { get; set; }

        public int DayCount => EndDate.DayNumber - StartDate.DayNumber + 1;

        public bool Covers(DateOnly date) => date >= StartDate && date <= EndDate;

        public bool Overlaps(DateOnly from, DateOnly to) => StartDate <= to && EndDate >= from;

        // Pending and approved requests are the live ones: they block overlapping requests and
        // count towards a balance (approved as used, pending as requested).
        public bool IsLive => Status is TimeOffStatus.Pending or TimeOffStatus.Approved;
    }
}

namespace Lanyard.Infrastructure.DTO.Scheduling;

// One row of the Manage > Rota > Staff list: a company member with what they hold today, so a
// manager can pick who to change and see the result straight after.
public record StaffRotaSummary(
    string UserId,
    string Name,
    List<string> LocationNames,
    List<StaffRotaPosition> Positions,
    ResolvedContract Contract,
    bool HasContractOverride,
    int AllowanceOverrideCount);

public record StaffRotaPosition(Guid PositionId, string Name, int ColorIndex, bool IsPrimary);

public enum BulkPositionMode
{
    // Adds the positions to what each person already holds.
    Add,

    // Takes the positions away, leaving the rest.
    Remove,

    // Each person ends up holding exactly these positions.
    Replace
}

// PrimaryPositionId must be one of PositionIds. For Add it's optional: people who had no
// primary get the first added position, and everyone else keeps theirs unless one is given.
public record BulkPositionChange(BulkPositionMode Mode, List<Guid> PositionIds, Guid? PrimaryPositionId);

public enum BulkFieldMode
{
    // Leave each person's current value alone.
    Keep,

    // Remove the personal override so the value is inherited again.
    Inherit,

    // Everyone gets this value.
    Set
}

public record BulkFieldChange<T>(BulkFieldMode Mode, T? Value = null) where T : struct
{
    public static readonly BulkFieldChange<T> Keep = new(BulkFieldMode.Keep);

    public T? Apply(T? current) => Mode switch
    {
        BulkFieldMode.Inherit => null,
        BulkFieldMode.Set => Value,
        _ => current
    };
}

public record BulkContractChange(
    BulkFieldChange<int> MinShiftsPerWeek,
    BulkFieldChange<decimal> MinShiftLengthHours,
    BulkFieldChange<decimal> MinHoursPerWeek,
    BulkFieldChange<decimal> MaxHoursPerWeek)
{
    public bool ChangesAnything =>
        MinShiftsPerWeek.Mode != BulkFieldMode.Keep || MinShiftLengthHours.Mode != BulkFieldMode.Keep
        || MinHoursPerWeek.Mode != BulkFieldMode.Keep || MaxHoursPerWeek.Mode != BulkFieldMode.Keep;
}

public enum BulkAllowanceMode
{
    Inherit,
    Unlimited,
    Amount
}

// One time-off type's new value for everyone selected. Types left out of the list are untouched.
public record BulkAllowanceChange(Guid TimeOffTypeId, BulkAllowanceMode Mode, decimal Hours = 0m);

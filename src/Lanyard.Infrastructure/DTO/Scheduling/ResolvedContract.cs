using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.DTO.Scheduling;

// One effective contract value plus the tier it was inherited from. Value is null (Source None)
// when no tier sets it, which the compliance check reads as "no requirement".
public record ContractValue<T>(T? Value, ContractTier Source) where T : struct
{
    public static readonly ContractValue<T> Unset = new(null, ContractTier.None);
}

// The per-field coalesce of user -> primary position -> company ContractRequirement rows.
public record ResolvedContract(
    ContractValue<int> MinShiftsPerWeek,
    ContractValue<decimal> MinShiftLengthHours,
    ContractValue<decimal> MinHoursPerWeek,
    ContractValue<decimal> MaxHoursPerWeek)
{
    public static readonly ResolvedContract Empty = new(
        ContractValue<int>.Unset, ContractValue<decimal>.Unset, ContractValue<decimal>.Unset, ContractValue<decimal>.Unset);

    public bool HasAnyRequirement =>
        MinShiftsPerWeek.Value is not null || MinShiftLengthHours.Value is not null
        || MinHoursPerWeek.Value is not null || MaxHoursPerWeek.Value is not null;
}

namespace Lanyard.Infrastructure.DTO.Scheduling;

// What the user editor shows about someone's young worker dates. UpdatedByName is null when the
// manager who set it has since been erased.
public record YoungWorkerStatusView(
    DateOnly? UnderEighteenUntil,
    DateOnly? SchoolLeavingAgeUntil,
    DateTime UpdateDate,
    string? UpdatedByName);

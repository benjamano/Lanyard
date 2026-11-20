namespace Lanyard.Infrastructure.DTO.Scheduling;

// What the user editor shows about someone under 18: their date of birth and the dates worked out
// from it. UpdatedByName is null when the manager who set it has since been erased.
public record YoungWorkerStatusView(
    DateOnly DateOfBirth,
    DateOnly EighteenthBirthday,
    DateOnly SchoolLeavingDate,
    DateTime UpdateDate,
    string? UpdatedByName);

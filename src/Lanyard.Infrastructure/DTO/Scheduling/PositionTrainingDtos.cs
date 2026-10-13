namespace Lanyard.Infrastructure.DTO.Scheduling;

// How many people in a position are up to date on a set of courses right now. Shown under the
// required-training picker, worked out from the ticked courses before they're saved, so a manager
// sees how many people a requirement would stop picking up shifts. LocationName is set when it
// only counts one location's staff (a Manager's view).
public record TrainingCoverage(int Holders, int UpToDate, string? LocationName);

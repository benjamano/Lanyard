using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

public interface IRotaService
{
    // Everything the rota builder needs for one location and an inclusive local date range (a week
    // or a month). Contract warnings are evaluated per Monday-week for every week the range touches,
    // using whole weeks even where the range starts or ends mid-week. includeAllMembers adds
    // location members who hold no position.
    Task<Result<RotaRangeView>> GetRangeViewAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to, bool includeAllMembers = false);

    // Creates (Id empty) or updates a shift. Fails on invalid times, a user who isn't a member of
    // the location, or an overlap with another of the person's shifts at any location. Non-blocking
    // issues (e.g. a position the person doesn't hold) come back as Warnings.
    // Moving a *published* shift to a different person removes it from the first person (who is
    // told on the next publish) and creates a new draft for the second.
    Task<Result<ShiftSaveResult>> SaveShiftAsync(LocationScope scope, Shift shift, string actingUserId);

    // A published shift stays visible (struck through) to managers until the removal is published;
    // a draft simply disappears.
    Task<Result<bool>> DeleteShiftAsync(LocationScope scope, Guid shiftId, string actingUserId);

    // Copies every active shift from offsetDays earlier into [targetFrom, targetTo] as drafts,
    // keeping local wall-clock times. offsetDays must be a positive multiple of 7 so weekdays line
    // up. Shifts for people who've left the location, or that would overlap an existing shift, are
    // skipped and reported.
    Task<Result<CopyRangeResult>> CopyRangeAsync(LocationScope scope, int locationId, DateOnly targetFrom, DateOnly targetTo, int offsetDays, string actingUserId);

    // Publishes every draft, changed and removed shift starting in [from, to] at the location, and
    // returns who was affected and how - the basis for "your rota has changed" notifications.
    Task<Result<PublishResult>> PublishRangeAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to, string actingUserId);

    // Published, active shifts for one person across all locations, for My Shifts.
    Task<Result<List<Shift>>> GetShiftsForUserAsync(string userId, DateTime fromUtc, DateTime toUtc);
}

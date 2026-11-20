using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

// Learns shift templates (and private hire staffing) from the rota a location actually published
// (TemplateLearningRules), checks how close Auto-build with them gets to that rota, and saves the
// suggestions a manager picks. Nothing is saved until ApplyAsync.
public interface ITemplateLearningService
{
    public const int MinWeeks = 2;
    public const int MaxWeeks = 26;

    // The monthly suggestions look back this many whole weeks.
    public const int MonthlyWeeks = 8;

    // A location with no templates is offered them once this many of those weeks have a published rota.
    public const int FirstSetupWeeks = 4;

    // The last `weeks` whole weeks (Monday to Sunday) before this week.
    Task<Result<TemplateLearningResult>> LearnAsync(LocationScope scope, int locationId, int weeks);

    // Creates or updates the chosen templates, and sets the chosen hire staffing. Returns how many
    // were saved; any that couldn't be are named in the error.
    Task<Result<int>> ApplyAsync(LocationScope scope, int locationId, IReadOnlyCollection<LearnedTemplate> templates,
        IReadOnlyCollection<LearnedHireStaffing> hireStaffing, string actingUserId);

    // The latest monthly suggestions for the location if they haven't been reviewed or dismissed.
    Task<Result<TemplateSuggestionRun?>> GetOpenSuggestionsAsync(LocationScope scope, int locationId);

    // Marks the location's open monthly suggestions as dealt with (reviewed or dismissed).
    Task<Result<bool>> DismissSuggestionsAsync(LocationScope scope, int locationId, string actingUserId);
}

using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;

namespace Lanyard.Application.Services.Scheduling;

// Learns shift templates (and private hire staffing) from the rota a location actually published
// (TemplateLearningRules), checks how close Auto-build with them gets to that rota, and saves the
// suggestions a manager picks. Nothing is saved until ApplyAsync.
public interface ITemplateLearningService
{
    public const int MinWeeks = 2;
    public const int MaxWeeks = 26;

    // The last `weeks` whole weeks (Monday to Sunday) before this week.
    Task<Result<TemplateLearningResult>> LearnAsync(LocationScope scope, int locationId, int weeks);

    // Creates or updates the chosen templates, and sets the chosen hire staffing. Returns how many
    // were saved; any that couldn't be are named in the error.
    Task<Result<int>> ApplyAsync(LocationScope scope, int locationId, IReadOnlyCollection<LearnedTemplate> templates,
        IReadOnlyCollection<LearnedHireStaffing> hireStaffing, string actingUserId);
}

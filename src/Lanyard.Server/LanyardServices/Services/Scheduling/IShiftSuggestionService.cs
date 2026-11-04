using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

public interface IShiftSuggestionService
{
    // Who at the shift's location could work it, best first (see ShiftSuggestionRules), and who
    // couldn't and why. The shift can be saved (an open shift being edited) or not yet saved (the
    // add dialog). Advice only: nothing is saved and nobody is told. Managers of the location and
    // admins only.
    Task<Result<ShiftSuggestions>> SuggestAsync(LocationScope scope, Shift shift);
}

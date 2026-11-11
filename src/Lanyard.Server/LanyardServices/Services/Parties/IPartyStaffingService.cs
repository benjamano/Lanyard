using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Parties;

// How many extra staff each kind of party needs (PartyStaffingRule), which Auto-build on the rota
// adds for every booked party. Same access rule as the rest of party settings: Admin, or a Manager
// at that location.
public interface IPartyStaffingService
{
    // The location's active rules, default rule (any other type) last, with positions loaded.
    Task<Result<List<PartyStaffingRule>>> GetRulesAsync(LocationScope scope, int locationId);

    // Creates (Id empty) or updates a rule.
    Task<Result<PartyStaffingRule>> SaveRuleAsync(LocationScope scope, PartyStaffingRule rule, string actingUserId);

    // Archives a rule. Shifts Auto-build already added are left on the rota.
    Task<Result<bool>> RemoveRuleAsync(LocationScope scope, Guid ruleId, string actingUserId);
}

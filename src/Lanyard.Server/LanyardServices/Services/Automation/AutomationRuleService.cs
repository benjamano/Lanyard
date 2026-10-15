#nullable enable

using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services;

public class AutomationRuleService(
    IDbContextFactory<ApplicationDbContext> factory,
    AutomationEngineService engineService) : IAutomationRuleService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly AutomationEngineService _engineService = engineService;

    public async Task<Result<IEnumerable<AutomationRule>>> GetRulesAsync()
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();
            IEnumerable<AutomationRule> rules = await ctx.AutomationRules
                .Where(r => r.IsActive)
                .Include(r => r.Actions.Where(a => a.IsActive))
                .Include(r => r.Conditions.Where(c => c.IsActive))
                .AsNoTracking()
                .ToListAsync();
            return Result<IEnumerable<AutomationRule>>.Ok(rules);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<AutomationRule>>.Fail(ex.Message);
        }
    }

    public async Task<Result<AutomationRule?>> GetRuleAsync(Guid id)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();
            AutomationRule? rule = await ctx.AutomationRules
                .Include(r => r.Actions.Where(a => a.IsActive))
                .Include(r => r.Conditions.Where(c => c.IsActive))
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id);
            return Result<AutomationRule?>.Ok(rule);
        }
        catch (Exception ex)
        {
            return Result<AutomationRule?>.Fail(ex.Message);
        }
    }

    public async Task<Result<IEnumerable<AutomationRule>>> GetRulesByTriggerAsync(Guid triggerClientId, GameStatus triggerEvent)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();
            IEnumerable<AutomationRule> rules = await ctx.AutomationRules
                .Where(r => r.TriggerClientId == triggerClientId && r.TriggerEvent == triggerEvent && r.IsActive && r.IsEnabled)
                .Include(r => r.Actions.Where(a => a.IsActive))
                .Include(r => r.Conditions.Where(c => c.IsActive))
                .AsNoTracking()
                .ToListAsync();
            return Result<IEnumerable<AutomationRule>>.Ok(rules);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<AutomationRule>>.Fail(ex.Message);
        }
    }

    public async Task<Result<AutomationRule>> CreateRuleAsync(AutomationRule rule)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // CompanyId is normally 0 here and stamped on save; the tenant filter on the kiosk
            // lookup is what keeps a condition inside the caller's company.
            string? validationError = await ValidateRuleAsync(ctx, rule, rule.CompanyId == 0 ? null : rule.CompanyId);

            if (validationError != null)
            {
                return Result<AutomationRule>.Fail(validationError);
            }

            rule.CreateDate = DateTime.UtcNow;
            rule.IsActive = true;
            rule.IsEnabled = true;
            ctx.AutomationRules.Add(rule);
            await ctx.SaveChangesAsync();
            _engineService.InvalidateRuleCache();
            return Result<AutomationRule>.Ok(rule);
        }
        catch (Exception ex)
        {
            return Result<AutomationRule>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Copies the rule's trigger settings onto the stored rule and syncs its actions and
    /// conditions: new rows are added, changed rows updated, and rows missing from the incoming
    /// rule soft-deleted.
    /// </summary>
    /// <remarks>
    /// Deliberately not ctx.Update(rule) on the detached graph: that marks every child with a key
    /// set as Modified, so a row added while editing (its Guid is generated client-side) became an
    /// UPDATE of nothing and failed the save. It also overwrote IsEnabled with whatever the caller
    /// sent, re-enabling a disabled rule on edit; IsEnabled is only changed by SetRuleEnabledAsync.
    /// </remarks>
    public async Task<Result<AutomationRule>> UpdateRuleAsync(AutomationRule rule)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            AutomationRule? existing = await ctx.AutomationRules
                .Include(r => r.Actions)
                .Include(r => r.Conditions)
                .TagWithCallSite()
                .FirstOrDefaultAsync(r => r.Id == rule.Id && r.IsActive);

            if (existing == null)
            {
                return Result<AutomationRule>.Fail("Rule not found.");
            }

            string? validationError = await ValidateRuleAsync(ctx, rule, existing.CompanyId);

            if (validationError != null)
            {
                return Result<AutomationRule>.Fail(validationError);
            }

            existing.Name = rule.Name;
            existing.TriggerClientId = rule.TriggerClientId;
            existing.TriggerType = rule.TriggerType;
            existing.TriggerEvent = rule.TriggerEvent;
            existing.IdleThresholdMinutes = rule.IdleThresholdMinutes;
            existing.ScheduledTimeOfDay = rule.ScheduledTimeOfDay;
            existing.ScheduledDaysOfWeek = rule.ScheduledDaysOfWeek;
            existing.LastUpdateDate = DateTime.UtcNow;

            SyncActions(ctx, existing, rule.Actions);
            SyncConditions(ctx, existing, rule.Conditions);

            await ctx.SaveChangesAsync();
            _engineService.InvalidateRuleCache();
            return Result<AutomationRule>.Ok(existing);
        }
        catch (Exception ex)
        {
            return Result<AutomationRule>.Fail(ex.Message);
        }
    }

    private static void SyncActions(ApplicationDbContext ctx, AutomationRule existing, IEnumerable<AutomationRuleAction> incoming)
    {
        List<AutomationRuleAction> wanted = incoming.Where(a => a.IsActive).ToList();
        HashSet<Guid> wantedIds = wanted.Select(a => a.Id).ToHashSet();

        foreach (AutomationRuleAction removed in existing.Actions.Where(a => a.IsActive && !wantedIds.Contains(a.Id)))
        {
            removed.IsActive = false;
        }

        foreach (AutomationRuleAction action in wanted)
        {
            AutomationRuleAction? current = existing.Actions.FirstOrDefault(a => a.Id == action.Id);

            if (current == null)
            {
                ctx.AutomationRuleActions.Add(new AutomationRuleAction
                {
                    Id = action.Id == Guid.Empty ? Guid.NewGuid() : action.Id,
                    AutomationRuleId = existing.Id,
                    ActionType = action.ActionType,
                    ParametersJson = action.ParametersJson,
                    SortOrder = action.SortOrder,
                    IsActive = true
                });

                continue;
            }

            current.ActionType = action.ActionType;
            current.ParametersJson = action.ParametersJson;
            current.SortOrder = action.SortOrder;
            current.IsActive = true;
        }
    }

    private static void SyncConditions(ApplicationDbContext ctx, AutomationRule existing, IEnumerable<AutomationRuleCondition> incoming)
    {
        List<AutomationRuleCondition> wanted = incoming.Where(c => c.IsActive).ToList();
        HashSet<Guid> wantedIds = wanted.Select(c => c.Id).ToHashSet();

        foreach (AutomationRuleCondition removed in existing.Conditions.Where(c => c.IsActive && !wantedIds.Contains(c.Id)))
        {
            removed.IsActive = false;
        }

        foreach (AutomationRuleCondition condition in wanted)
        {
            AutomationRuleCondition? current = existing.Conditions.FirstOrDefault(c => c.Id == condition.Id);

            if (current == null)
            {
                ctx.AutomationRuleConditions.Add(new AutomationRuleCondition
                {
                    Id = condition.Id == Guid.Empty ? Guid.NewGuid() : condition.Id,
                    AutomationRuleId = existing.Id,
                    ConditionType = condition.ConditionType,
                    ParametersJson = condition.ParametersJson,
                    SortOrder = condition.SortOrder,
                    IsActive = true
                });

                continue;
            }

            current.ConditionType = condition.ConditionType;
            current.ParametersJson = condition.ParametersJson;
            current.SortOrder = condition.SortOrder;
            current.IsActive = true;
        }
    }

    public async Task<Result<bool>> DeleteRuleAsync(Guid id)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();
            AutomationRule? rule = await ctx.AutomationRules.FindAsync(id);
            if (rule == null)
            {
                return Result<bool>.Fail("Rule not found.");
            }
            rule.IsActive = false;
            rule.LastUpdateDate = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
            _engineService.InvalidateRuleCache();
            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<AutomationRule>> SetRuleEnabledAsync(Guid id, bool isEnabled)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();
            AutomationRule? rule = await ctx.AutomationRules.FindAsync(id);
            if (rule == null)
            {
                return Result<AutomationRule>.Fail("Rule not found.");
            }
            rule.IsEnabled = isEnabled;
            rule.LastUpdateDate = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
            _engineService.InvalidateRuleCache();
            _engineService.NotifyRuleEnabledChanged(id, isEnabled);
            return Result<AutomationRule>.Ok(rule);
        }
        catch (Exception ex)
        {
            return Result<AutomationRule>.Fail(ex.Message);
        }
    }

    // A rule with a misconfigured trigger or condition can never fire, and nothing downstream
    // would report that - ProcessIdleRulesAsync/ProcessScheduledRulesAsync simply skip it - so
    // reject it at the point of saving instead.
    private static async Task<string?> ValidateRuleAsync(ApplicationDbContext ctx, AutomationRule rule, int? ruleCompanyId)
    {
        string? triggerError = ValidateTrigger(rule.TriggerType, rule.IdleThresholdMinutes, rule.ScheduledTimeOfDay, rule.ScheduledDaysOfWeek);

        if (triggerError != null)
        {
            return triggerError;
        }

        string? conditionError = ValidateConditions(rule.Conditions);

        if (conditionError != null)
        {
            return conditionError;
        }

        List<Guid> conditionClientIds = rule.Conditions
            .Where(c => c.IsActive && c.ConditionType == AutomationConditionType.ClientGameStatus)
            .Select(c => AutomationConditionParameters.TryParse(c.ParametersJson, out ClientGameStatusConditionParameters? p) ? p.ClientId : Guid.Empty)
            .Distinct()
            .ToList();

        if (conditionClientIds.Count > 0)
        {
            // The context is tenant-filtered for signed-in callers, so a kiosk in another company
            // is simply not found. The explicit company check covers an existing rule edited
            // through an unfiltered (system) context.
            int foundCount = await ctx.Clients
                .Where(c => conditionClientIds.Contains(c.Id) && (ruleCompanyId == null || c.CompanyId == ruleCompanyId))
                .AsNoTracking()
                .TagWithCallSite()
                .CountAsync();

            if (foundCount != conditionClientIds.Count)
            {
                return "A game status condition refers to a kiosk that doesn't exist.";
            }
        }

        List<int> conditionLocationIds = rule.Conditions
            .Where(c => c.IsActive && c.ConditionType == AutomationConditionType.DuringOpeningHours)
            .Select(c => AutomationConditionParameters.TryParse(c.ParametersJson, out OpeningHoursConditionParameters? p) ? p.LocationId : 0)
            .Distinct()
            .ToList();

        if (conditionLocationIds.Count > 0)
        {
            // Locations aren't tenant-filtered rows (login has to list them), so the company check
            // has to be explicit. A new rule takes the signed-in caller's company.
            int? companyId = ruleCompanyId ?? (ctx.TenantFilterDisabled ? null : ctx.TenantCompanyId);

            int foundCount = await ctx.Locations
                .Where(l => conditionLocationIds.Contains(l.Id) && l.IsActive && (companyId == null || l.CompanyId == companyId))
                .AsNoTracking()
                .TagWithCallSite()
                .CountAsync();

            if (foundCount != conditionLocationIds.Count)
            {
                return "An opening hours condition refers to a location that doesn't exist.";
            }
        }

        return null;
    }

    /// <summary>
    /// Public so AddEditAutomationRuleDialog can gate its conditions step against the same rule
    /// CreateRuleAsync/UpdateRuleAsync will apply. Doesn't check that kiosks or locations exist -
    /// that needs the database.
    /// </summary>
    public static string? ValidateConditions(IEnumerable<AutomationRuleCondition> conditions)
    {
        foreach (AutomationRuleCondition condition in conditions.Where(c => c.IsActive))
        {
            string? error = AutomationConditionParameters.Validate(condition.ConditionType, condition.ParametersJson);

            if (error != null)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>
    /// Public so AddEditAutomationRuleDialog can gate its wizard's "Next" step against the exact
    /// same rule CreateRuleAsync/UpdateRuleAsync will apply, instead of hand-mirroring it.
    /// </summary>
    public static string? ValidateTrigger(
        AutomationTriggerType triggerType,
        int? idleThresholdMinutes,
        TimeOnly? scheduledTimeOfDay,
        string? scheduledDaysOfWeek)
    {
        switch (triggerType)
        {
            case AutomationTriggerType.ClientIdle:
                if (!idleThresholdMinutes.HasValue)
                {
                    return "An idle rule needs an idle threshold in minutes.";
                }

                if (idleThresholdMinutes.Value <= 0)
                {
                    return "An idle rule's threshold must be greater than zero minutes.";
                }

                return null;

            case AutomationTriggerType.Scheduled:
                if (!scheduledTimeOfDay.HasValue)
                {
                    return "A scheduled rule needs a time of day.";
                }

                if (!AutomationScheduleDays.TryParse(scheduledDaysOfWeek, out _))
                {
                    return "A scheduled rule's days of week are not valid.";
                }

                return null;

            default:
                return null;
        }
    }
}

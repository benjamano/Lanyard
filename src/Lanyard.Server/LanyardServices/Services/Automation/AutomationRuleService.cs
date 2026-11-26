#nullable enable

using Lanyard.Application.Services.AudioClips;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Features;
using Lanyard.Application.Services.Parties;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services;

// currentUser is optional so the many tests that only exercise saving can keep constructing the
// service with a factory and an engine; DI always supplies it.
public class AutomationRuleService(
    IDbContextFactory<ApplicationDbContext> factory,
    IAutomationEngineService engineService,
    ICurrentUserAccessor? currentUser = null,
    IAudioClipPlaybackService? clipPlayback = null) : IAutomationRuleService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IAutomationEngineService _engineService = engineService;
    private readonly ICurrentUserAccessor? _currentUser = currentUser;

    public async Task<Result<IEnumerable<AutomationRule>>> GetRulesAsync()
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();
            IEnumerable<AutomationRule> rules = await ctx.AutomationRules
                .Where(r => r.IsActive)
                .Include(r => r.Actions.Where(a => a.IsActive))
                .Include(r => r.Conditions.Where(c => c.IsActive))
                // Oldest first, so the list doesn't reshuffle between loads and a copy lands at the end.
                .OrderBy(r => r.CreateDate)
                .ThenBy(r => r.Name)
                .AsNoTracking()
                .TagWithCallSite()
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
            await PrefetchClipsAsync(ctx, rule.Id);
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
            existing.TriggerThreshold = rule.TriggerThreshold;
            existing.TriggerScoreScope = rule.TriggerScoreScope;
            existing.TriggerTeam = rule.TriggerTeam;
            existing.PartyLocationId = rule.PartyLocationId;
            existing.PartyMilestone = rule.PartyMilestone;
            existing.PartyLeadMinutes = rule.PartyLeadMinutes;
            existing.PartyRoom = rule.PartyRoom;
            existing.LastUpdateDate = DateTime.UtcNow;

            SyncActions(ctx, existing, rule.Actions);
            SyncConditions(ctx, existing, rule.Conditions);

            await ctx.SaveChangesAsync();
            _engineService.InvalidateRuleCache();
            await PrefetchClipsAsync(ctx, existing.Id);
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

            if (isEnabled)
            {
                await PrefetchClipsAsync(ctx, id);
            }

            return Result<AutomationRule>.Ok(rule);
        }
        catch (Exception ex)
        {
            return Result<AutomationRule>.Fail(ex.Message);
        }
    }

    public async Task<Result<AutomationRule>> DuplicateRuleAsync(Guid id)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // Tenant-filtered, so another company's rule is simply not found.
            AutomationRule? source = await ctx.AutomationRules
                .Where(r => r.Id == id && r.IsActive)
                .Include(r => r.Actions.Where(a => a.IsActive))
                .Include(r => r.Conditions.Where(c => c.IsActive))
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync();

            if (source == null)
            {
                return Result<AutomationRule>.Fail("Rule not found.");
            }

            string name = await NextCopyNameAsync(ctx, source.Name);

            // A brand-new graph rather than the loaded entities with their keys changed, so EF
            // never confuses the copy's rows with the source's.
            Guid copyId = Guid.NewGuid();

            AutomationRule copy = new()
            {
                Id = copyId,
                CompanyId = source.CompanyId,
                Name = name,
                TriggerClientId = source.TriggerClientId,
                TriggerType = source.TriggerType,
                TriggerEvent = source.TriggerEvent,
                IdleThresholdMinutes = source.IdleThresholdMinutes,
                ScheduledTimeOfDay = source.ScheduledTimeOfDay,
                ScheduledDaysOfWeek = source.ScheduledDaysOfWeek,
                TriggerThreshold = source.TriggerThreshold,
                TriggerScoreScope = source.TriggerScoreScope,
                TriggerTeam = source.TriggerTeam,
                PartyLocationId = source.PartyLocationId,
                PartyMilestone = source.PartyMilestone,
                PartyLeadMinutes = source.PartyLeadMinutes,
                PartyRoom = source.PartyRoom,
                // Off until someone checks it, so a copy can't fire alongside its original.
                IsEnabled = false,
                IsActive = true,
                CreateDate = DateTime.UtcNow,
                Actions = source.Actions
                    .OrderBy(a => a.SortOrder)
                    .Select(a => new AutomationRuleAction
                    {
                        Id = Guid.NewGuid(),
                        AutomationRuleId = copyId,
                        ActionType = a.ActionType,
                        ParametersJson = a.ParametersJson,
                        SortOrder = a.SortOrder,
                        IsActive = true
                    })
                    .ToList(),
                Conditions = source.Conditions
                    .OrderBy(c => c.SortOrder)
                    .Select(c => new AutomationRuleCondition
                    {
                        Id = Guid.NewGuid(),
                        AutomationRuleId = copyId,
                        ConditionType = c.ConditionType,
                        ParametersJson = c.ParametersJson,
                        SortOrder = c.SortOrder,
                        IsActive = true
                    })
                    .ToList()
            };

            // The source may have gone stale since it was saved (a kiosk or scene removed), and a
            // copy that couldn't be saved by hand shouldn't be created this way either.
            string? validationError = await ValidateRuleAsync(ctx, copy, source.CompanyId);

            if (validationError != null)
            {
                return Result<AutomationRule>.Fail(validationError);
            }

            ctx.AutomationRules.Add(copy);
            await ctx.SaveChangesAsync();

            // Disabled, so the engine wouldn't pick it up anyway, but the cache is only ever
            // trusted when nothing has changed under it.
            _engineService.InvalidateRuleCache();

            return Result<AutomationRule>.Ok(copy);
        }
        catch (Exception ex)
        {
            return Result<AutomationRule>.Fail($"Couldn't copy the rule: {ex.Message}");
        }
    }

    // A clip the rule plays is downloaded by its kiosk now, so it plays the instant the rule fires
    // rather than after a download. Only an optimisation: failures are logged by the playback service.
    private async Task PrefetchClipsAsync(ApplicationDbContext ctx, Guid ruleId)
    {
        if (clipPlayback is null)
        {
            return;
        }

        List<string> parameters = await ctx.AutomationRuleActions
            .AsNoTracking()
            .TagWithCallSite()
            .Where(a => a.AutomationRuleId == ruleId && a.IsActive && a.ActionType == AutomationActionTypes.PlayAudioClip)
            .Select(a => a.ParametersJson)
            .ToListAsync();

        IEnumerable<Guid> kiosks = parameters
            .Select(json => AutomationActionTargets.TryRead(AutomationActionTypes.PlayAudioClip, json, out AutomationActionTarget target) ? target.ClientId : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct();

        foreach (Guid kiosk in kiosks)
        {
            await clipPlayback.PrefetchClipsForKioskAsync(kiosk);
        }
    }

    public async Task<Result<List<AutomationActionDescription>>> DescribeActionsAsync(Guid ruleId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            AutomationRule? rule = await ctx.AutomationRules
                .Where(r => r.Id == ruleId && r.IsActive)
                .Include(r => r.Actions.Where(a => a.IsActive))
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync();

            if (rule == null)
            {
                return Result<List<AutomationActionDescription>>.Fail("Rule not found.");
            }

            List<AutomationActionTarget> targets = rule.Actions
                .Select(a => AutomationActionTargets.TryRead(a.ActionType, a.ParametersJson, out AutomationActionTarget t) ? t : null)
                .OfType<AutomationActionTarget>()
                .ToList();

            List<Guid> kioskIds = targets.Select(t => t.ClientId).Where(id => id != Guid.Empty).Distinct().ToList();
            List<Guid> IdsOf(AutomationActionTargetKind kind) => targets.SelectMany(t => t.References).Where(r => r.Kind == kind).Select(r => r.Id).Distinct().ToList();
            List<Guid> playlistIds = IdsOf(AutomationActionTargetKind.Playlist);
            List<Guid> programIds = IdsOf(AutomationActionTargetKind.ProjectionProgram);
            List<Guid> sceneIds = IdsOf(AutomationActionTargetKind.DmxScene);
            List<Guid> clipIds = IdsOf(AutomationActionTargetKind.AudioClip);

            // Tenant-filtered, so another company's kiosk or content shows as deleted, never by name.
            Dictionary<Guid, string> kiosks = await ctx.Clients.Where(c => kioskIds.Contains(c.Id)).AsNoTracking().TagWithCallSite().ToDictionaryAsync(c => c.Id, c => c.Name);
            Dictionary<(AutomationActionTargetKind, Guid), string> names = [];

            foreach (var x in await ctx.Playlists.Where(x => playlistIds.Contains(x.Id)).Select(x => new { x.Id, x.Name }).AsNoTracking().TagWithCallSite().ToListAsync())
            {
                names[(AutomationActionTargetKind.Playlist, x.Id)] = x.Name;
            }

            foreach (var x in await ctx.ProjectionPrograms.Where(x => programIds.Contains(x.Id)).Select(x => new { x.Id, x.Name }).AsNoTracking().TagWithCallSite().ToListAsync())
            {
                names[(AutomationActionTargetKind.ProjectionProgram, x.Id)] = x.Name;
            }

            foreach (var x in await ctx.DmxScenes.Where(x => sceneIds.Contains(x.Id) && x.IsActive).Select(x => new { x.Id, x.Name }).AsNoTracking().TagWithCallSite().ToListAsync())
            {
                names[(AutomationActionTargetKind.DmxScene, x.Id)] = x.Name;
            }

            foreach (var x in await ctx.AudioClips.Where(x => clipIds.Contains(x.Id)).Select(x => new { x.Id, x.Name }).AsNoTracking().TagWithCallSite().ToListAsync())
            {
                names[(AutomationActionTargetKind.AudioClip, x.Id)] = x.Name;
            }

            List<AutomationActionDescription> descriptions = rule.Actions
                .OrderBy(a => a.SortOrder)
                .Select(a => new AutomationActionDescription(
                    a.Id,
                    a.ActionType,
                    AutomationActionDescriptions.Describe(
                        a.ActionType,
                        a.ParametersJson,
                        id => kiosks.GetValueOrDefault(id),
                        (kind, id) => names.GetValueOrDefault((kind, id)))))
                .ToList();

            return Result<List<AutomationActionDescription>>.Ok(descriptions);
        }
        catch (Exception ex)
        {
            return Result<List<AutomationActionDescription>>.Fail($"Couldn't read the rule's actions: {ex.Message}");
        }
    }

    public async Task<Result<AutomationTestRunResult>> TestRunRuleAsync(Guid ruleId, bool checkConditions)
    {
        try
        {
            if (_currentUser == null)
            {
                return Result<AutomationTestRunResult>.Fail("Only a signed-in user can test a rule.");
            }

            Result<string> userId = await _currentUser.GetCurrentUserIdAsync();

            if (!userId.IsSuccess || string.IsNullOrEmpty(userId.Data))
            {
                return Result<AutomationTestRunResult>.Fail("Only a signed-in user can test a rule.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // Through the caller's tenant-filtered context, so the engine (which isn't filtered) is
            // only ever handed the caller's own company.
            int? companyId = await ctx.AutomationRules
                .AsNoTracking()
                .TagWithCallSite()
                .Where(r => r.Id == ruleId && r.IsActive)
                .Select(r => (int?)r.CompanyId)
                .FirstOrDefaultAsync();

            if (companyId is not int company)
            {
                return Result<AutomationTestRunResult>.Fail("Rule not found.");
            }

            var user = await ctx.Users
                .Where(u => u.Id == userId.Data)
                .Select(u => new { Name = (u.FirstName + " " + u.LastName).Trim(), u.UserName })
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync();

            string displayName = !string.IsNullOrWhiteSpace(user?.Name) ? user.Name : user?.UserName ?? "an Admin";

            return await _engineService.TestRunRuleAsync(new AutomationTestRunRequest(ruleId, company, userId.Data, displayName, checkConditions));
        }
        catch (Exception ex)
        {
            return Result<AutomationTestRunResult>.Fail($"The test couldn't run: {ex.Message}");
        }
    }

    // "Copy of X", then "Copy of X (2)", "(3)"... among the company's live rules.
    private static async Task<string> NextCopyNameAsync(ApplicationDbContext ctx, string sourceName)
    {
        string baseName = $"Copy of {sourceName}";

        HashSet<string> taken = (await ctx.AutomationRules
            .Where(r => r.IsActive && r.Name.StartsWith(baseName))
            .Select(r => r.Name)
            .AsNoTracking()
            .TagWithCallSite()
            .ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(baseName))
        {
            return baseName;
        }

        int n = 2;

        while (taken.Contains($"{baseName} ({n})"))
        {
            n++;
        }

        return $"{baseName} ({n})";
    }

    // A rule with a misconfigured trigger or condition can never fire, and nothing downstream
    // would report that - ProcessIdleRulesAsync/ProcessScheduledRulesAsync simply skip it - so
    // reject it at the point of saving instead.
    private static async Task<string?> ValidateRuleAsync(ApplicationDbContext ctx, AutomationRule rule, int? ruleCompanyId)
    {
        NormalisePartyFields(rule);

        string? triggerError = ValidateTrigger(rule.TriggerType, rule.IdleThresholdMinutes, rule.ScheduledTimeOfDay, rule.ScheduledDaysOfWeek, rule.TriggerThreshold, rule.TriggerScoreScope)
            ?? (rule.TriggerType == AutomationTriggerType.PartyMilestone
                ? PartyTriggerRules.Validate(rule.PartyLocationId, rule.PartyMilestone, rule.PartyLeadMinutes, rule.PartyRoom)
                : null);

        if (triggerError != null)
        {
            return triggerError;
        }

        if (rule.TriggerType == AutomationTriggerType.PartyMilestone)
        {
            string? partyError = await ValidatePartyLocationAsync(ctx, rule.PartyLocationId!.Value, ruleCompanyId);

            if (partyError != null)
            {
                return partyError;
            }
        }

        string? conditionError = ValidateConditions(rule.Conditions);

        if (conditionError != null)
        {
            return conditionError;
        }

        // The kiosk that fires the rule, under the same company rule as the condition kiosks below.
        bool triggerClientFound = await ctx.Clients
            .Where(c => c.Id == rule.TriggerClientId && (ruleCompanyId == null || c.CompanyId == ruleCompanyId))
            .AsNoTracking()
            .TagWithCallSite()
            .AnyAsync();

        if (!triggerClientFound)
        {
            return "The kiosk this rule is for doesn't exist.";
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

        List<int> partyConditionLocationIds = rule.Conditions
            .Where(c => c.IsActive && c.ConditionType == AutomationConditionType.PartyInProgress)
            .Select(c => AutomationConditionParameters.TryParse(c.ParametersJson, out PartyInProgressConditionParameters? p) ? p.LocationId : 0)
            .Distinct()
            .ToList();

        if (partyConditionLocationIds.Count > 0)
        {
            // Same explicit company check as opening hours: locations aren't tenant-filtered.
            int? companyId = ruleCompanyId ?? (ctx.TenantFilterDisabled ? null : ctx.TenantCompanyId);

            int foundCount = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Where(l => partyConditionLocationIds.Contains(l.Id) && l.IsActive && (companyId == null || l.CompanyId == companyId))
                .CountAsync();

            if (foundCount != partyConditionLocationIds.Count)
            {
                return "A party condition refers to a location that doesn't exist.";
            }
        }

        return await ValidateActionTargetsAsync(ctx, rule, ruleCompanyId);
    }

    // Party fields only mean something on a party-time rule; anything else keeps them null, so a
    // rule switched from Party time to another trigger doesn't carry stale party settings around.
    private static void NormalisePartyFields(AutomationRule rule)
    {
        if (rule.TriggerType != AutomationTriggerType.PartyMilestone)
        {
            rule.PartyLocationId = null;
            rule.PartyMilestone = null;
            rule.PartyLeadMinutes = null;
            rule.PartyRoom = null;
            return;
        }

        rule.PartyRoom = PartyRooms.Normalise(rule.PartyRoom);
    }

    // Locations aren't tenant-filtered rows, so the company check is explicit (a new rule takes the
    // signed-in caller's company), and a company with Parties switched off can't add party rules.
    private static async Task<string?> ValidatePartyLocationAsync(ApplicationDbContext ctx, int locationId, int? ruleCompanyId)
    {
        int? companyId = ruleCompanyId ?? (ctx.TenantFilterDisabled ? null : ctx.TenantCompanyId);

        int? locationCompanyId = await ctx.Locations
            .AsNoTracking()
            .TagWithCallSite()
            .Where(l => l.Id == locationId && l.IsActive && (companyId == null || l.CompanyId == companyId))
            .Select(l => (int?)l.CompanyId)
            .FirstOrDefaultAsync();

        if (locationCompanyId is not int owner)
        {
            return "A party time rule refers to a location that doesn't exist.";
        }

        if (!await CompanyFeatureQueries.IsEnabledForCompanyAsync(ctx, owner, CompanyFeature.Parties))
        {
            return "Parties is switched off for this company, so a party time rule can't be saved.";
        }

        return null;
    }

    // An action's kiosk, and the playlist, program, scene or clip it plays, come from
    // ParametersJson, which the executors trust (they look kiosks up through the unfiltered system
    // context). So every one of them must be the rule's own company's, and a scene must be on the
    // action's kiosk. The engine checks the same again before it runs anything.
    private static async Task<string?> ValidateActionTargetsAsync(ApplicationDbContext ctx, AutomationRule rule, int? ruleCompanyId)
    {
        foreach (AutomationRuleAction action in rule.Actions.Where(a => a.IsActive && AutomationActionTargets.IsKnown(a.ActionType)))
        {
            string described = AutomationActionTargets.Describe(action.ActionType);
            string article = described.StartsWith("audio") ? "An" : "A";

            if (!AutomationActionTargets.TryRead(action.ActionType, action.ParametersJson, out AutomationActionTarget target))
            {
                return $"{article} {described} action couldn't be read. Remove it and add it again.";
            }

            // Not configured yet: the executor reports that when it runs, as it always has.
            if (target.ClientId == Guid.Empty)
            {
                continue;
            }

            bool clientFound = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(c => c.Id == target.ClientId && (ruleCompanyId == null || c.CompanyId == ruleCompanyId));

            if (!clientFound)
            {
                return $"{article} {described} action refers to a kiosk that doesn't exist.";
            }

            foreach ((AutomationActionTargetKind kind, Guid id) in target.References)
            {
                bool found = kind switch
                {
                    AutomationActionTargetKind.DmxScene => await ctx.DmxScenes
                        .AsNoTracking()
                        .TagWithCallSite()
                        .AnyAsync(x => x.Id == id && x.ClientId == target.ClientId && x.IsActive && (ruleCompanyId == null || x.CompanyId == ruleCompanyId)),
                    AutomationActionTargetKind.Playlist => await ctx.Playlists
                        .AsNoTracking()
                        .TagWithCallSite()
                        .AnyAsync(x => x.Id == id && (ruleCompanyId == null || x.CompanyId == ruleCompanyId)),
                    AutomationActionTargetKind.ProjectionProgram => await ctx.ProjectionPrograms
                        .AsNoTracking()
                        .TagWithCallSite()
                        .AnyAsync(x => x.Id == id && (ruleCompanyId == null || x.CompanyId == ruleCompanyId)),
                    AutomationActionTargetKind.AudioClip => await ctx.AudioClips
                        .AsNoTracking()
                        .TagWithCallSite()
                        .AnyAsync(x => x.Id == id && (ruleCompanyId == null || x.CompanyId == ruleCompanyId)),
                    _ => false
                };

                if (!found)
                {
                    return kind == AutomationActionTargetKind.DmxScene
                        ? $"{article} {described} action refers to a scene that isn't on its kiosk."
                        : $"{article} {described} action refers to {(kind == AutomationActionTargetKind.AudioClip ? "an" : "a")} {AutomationActionTargets.Describe(kind)} that doesn't exist.";
                }
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
        string? scheduledDaysOfWeek,
        int? triggerThreshold = null,
        AutomationScoreScope? triggerScoreScope = null)
    {
        switch (triggerType)
        {
            case AutomationTriggerType.TimeRemainingBelow:
                if (!triggerThreshold.HasValue || triggerThreshold.Value <= 0)
                {
                    return "A time remaining rule needs a number of seconds greater than zero.";
                }

                return null;

            case AutomationTriggerType.ScoreThreshold:
                if (!triggerThreshold.HasValue || triggerThreshold.Value <= 0)
                {
                    return "A score rule needs a score greater than zero.";
                }

                if (!triggerScoreScope.HasValue || !System.Enum.IsDefined(triggerScoreScope.Value))
                {
                    return "A score rule needs to say whose score counts.";
                }

                return null;

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

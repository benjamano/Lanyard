using Lanyard.Infrastructure.Enum;
using Lanyard.Shared.Enum;

namespace Lanyard.Infrastructure.Models;

public class AutomationRule : ICompanyOwned
{
    public Guid Id { get; set; }
    public int CompanyId { get; set; }

    public required string Name { get; set; }

    public Guid TriggerClientId { get; set; }
    public Client? TriggerClient { get; set; }

    public AutomationTriggerType TriggerType { get; set; } = AutomationTriggerType.GameStatusTransition;

    // Only meaningful when TriggerType is GameStatusTransition; ignored for ClientIdle rules.
    public GameStatus TriggerEvent { get; set; }

    // Required when TriggerType is ClientIdle, null otherwise. AutomationRuleService rejects a
    // ClientIdle rule without one rather than letting a rule that can never fire be saved.
    public int? IdleThresholdMinutes { get; set; }

    // Required when TriggerType is Scheduled, null otherwise. Venue wall-clock time (UK, see
    // RotaTime) - the basis staff actually experience, whatever zone the server runs in.
    public TimeOnly? ScheduledTimeOfDay { get; set; }

    // Meaningful only when TriggerType is Scheduled. CSV of System.DayOfWeek integer values
    // (e.g. "1,2,3,4,5"); null or empty means every day. See AutomationScheduleDays for the
    // shared parse/serialize/match logic.
    public string? ScheduledDaysOfWeek { get; set; }

    // Seconds for TimeRemainingBelow, points for ScoreThreshold; null for every other trigger type.
    public int? TriggerThreshold { get; set; }

    // ScoreThreshold only: whose score counts. Null otherwise.
    public AutomationScoreScope? TriggerScoreScope { get; set; }

    // ScoreThreshold: limit to one team's players (AnyPlayer) or pick the team (Team). GameEnded:
    // the team that has to win. Null means any team.
    public Team? TriggerTeam { get; set; }

    // PartyMilestone only, null for every other trigger type. Parties at this location fire the
    // rule. Kept on the rule rather than taken from the associated kiosk, so moving a kiosk never
    // quietly points a rule at another venue's parties. No FK: locations are soft-deleted, and
    // AutomationRuleService checks it belongs to the rule's company on every save.
    public int? PartyLocationId { get; set; }

    // PartyMilestone only: which moment of the party, and how many minutes before it (0-120).
    public PartyMilestone? PartyMilestone { get; set; }
    public int? PartyLeadMinutes { get; set; }

    // PartyMilestone only, optional: only parties in this room (PartyBooking.Room, matched
    // trimmed and ignoring case). Null = any room at the location.
    public string? PartyRoom { get; set; }

    public bool IsActive { get; set; }
    public bool IsEnabled { get; set; } = true;

    public DateTime CreateDate { get; set; }
    public DateTime? LastUpdateDate { get; set; }

    public virtual List<AutomationRuleAction> Actions { get; set; } = [];

    // Extra "only if" checks, all of which must pass when the trigger fires (AND). A rule with no
    // active conditions behaves exactly as rules did before conditions existed.
    public virtual List<AutomationRuleCondition> Conditions { get; set; } = [];

    public virtual List<AutomationRuleExecution> Executions { get; set; } = [];
}

public class AutomationRuleCondition
{
    public Guid Id { get; set; }

    public Guid AutomationRuleId { get; set; }
    public AutomationRule? AutomationRule { get; set; }

    public AutomationConditionType ConditionType { get; set; }

    // Shape depends on ConditionType - see AutomationConditionParameters for the typed records.
    public string ParametersJson { get; set; } = "{}";

    public int SortOrder { get; set; }

    public bool IsActive { get; set; }
}

public class AutomationRuleAction
{
    public Guid Id { get; set; }

    public Guid AutomationRuleId { get; set; }
    public AutomationRule? AutomationRule { get; set; }

    public required string ActionType { get; set; }
    public string ParametersJson { get; set; } = "{}";

    public int SortOrder { get; set; }

    public bool IsActive { get; set; }
}

public class AutomationRuleExecution
{
    public Guid Id { get; set; }

    public Guid AutomationRuleId { get; set; }
    public AutomationRule? AutomationRule { get; set; }

    public required string RuleName { get; set; }

    public DateTime ExecutedAt { get; set; }
    public string TriggerEvent { get; set; } = string.Empty;
    public Guid TriggerClientId { get; set; }

    public bool OverallSuccess { get; set; }

    // Set when the trigger fired but a condition wasn't met, so no actions ran. Such a row has
    // OverallSuccess false and no action rows, and must not be read as a failure or as "fired".
    public string? SkipReason { get; set; }

    // The party a PartyMilestone rule fired (or was skipped) for: the first one, when parties
    // reaching the same moment together were merged into one run. Only the id is kept - never the
    // child's name - so the log holds no personal data; the log screen looks the party up live.
    // Set to null if the booking is ever deleted.
    public Guid? PartyBookingId { get; set; }

    // Set when an Admin ran the rule by hand with "Run now (test)" rather than its trigger firing.
    // A test row stays in the log but is never "the last time the rule ran": the scheduled
    // reseed and the dashboard widget's last run both leave it out.
    public bool IsTestRun { get; set; }

    // Who ran the test (AspNetUsers.Id), when IsTestRun is set. No FK, like the other audit ids.
    public string? TestRunByUserId { get; set; }

    public virtual List<AutomationRuleActionExecution> ActionExecutions { get; set; } = [];
}

public class AutomationRuleActionExecution
{
    public Guid Id { get; set; }

    public Guid AutomationRuleExecutionId { get; set; }
    public AutomationRuleExecution? AutomationRuleExecution { get; set; }

    public Guid AutomationRuleActionId { get; set; }
    public AutomationRuleAction? AutomationRuleAction { get; set; }

    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

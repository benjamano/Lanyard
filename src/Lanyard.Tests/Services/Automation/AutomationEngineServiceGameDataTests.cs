#nullable enable

using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Automation;

/// <summary>
/// TimeRemainingBelow and ScoreThreshold rules: fed the way the hub feeds them (EnqueueTransition
/// then EnqueueGameData for every publish), drained the way GameDataTriggerHostedService drains them.
/// </summary>
[TestClass]
public class AutomationEngineServiceGameDataTests
{
    private sealed record Harness(DbContextOptions<ApplicationDbContext> Options, AutomationEngineService Engine, RecordingActionExecutor Executor);

    private static readonly Guid KioskId = Guid.NewGuid();

    private static async Task<Harness> CreateHarnessAsync(params AutomationRule[] rules)
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.AppSettings.Add(new AppSetting { Id = Guid.NewGuid(), Key = "AutomationEngine.Enabled", Value = "true", CreateDate = DateTime.UtcNow });
            ctx.AutomationRules.AddRange(rules);
            await ctx.SaveChangesAsync();
        }

        Mock<ISystemDbContextFactory> factoryMock = new();
        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = new(factoryMock.Object, [executor], new LaserGameStatusStore(), TimeProvider.System, NullLogger<AutomationEngineService>.Instance);

        return new Harness(options, engine, executor);
    }

    private static AutomationRule Rule(
        AutomationTriggerType triggerType,
        int threshold,
        AutomationScoreScope? scope = null,
        Team? team = null,
        params AutomationRuleCondition[] conditions)
    {
        return new AutomationRule
        {
            Id = Guid.NewGuid(),
            CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId,
            Name = $"{triggerType} {threshold}",
            TriggerClientId = KioskId,
            TriggerType = triggerType,
            TriggerThreshold = threshold,
            TriggerScoreScope = scope,
            TriggerTeam = team,
            IsActive = true,
            IsEnabled = true,
            CreateDate = DateTime.UtcNow,
            Actions = [new AutomationRuleAction { Id = Guid.NewGuid(), ActionType = RecordingActionExecutor.TestActionType, IsActive = true }],
            Conditions = [.. conditions]
        };
    }

    private static LaserGameStatusDTO Status(GameStatus status, int timeRemaining = 0, params (int Gun, Team? Team, int Score)[] players)
    {
        return new LaserGameStatusDTO
        {
            Status = status,
            TimeRemainingSeconds = timeRemaining,
            PlayerScores = [.. players.Select(p => new PlayerScoreDTO { GunId = p.Gun, Team = p.Team, Score = p.Score })]
        };
    }

    // One kiosk publish, as the hub handles it, then everything the consumer has to do.
    private static async Task PublishAsync(Harness harness, LaserGameStatusDTO status)
    {
        harness.Engine.EnqueueTransition(KioskId, status.Status);
        harness.Engine.EnqueueGameData(KioskId, status);

        while (harness.Engine.GameDataReader.TryRead(out GameDataEvent? ev))
        {
            await harness.Engine.ProcessGameDataAsync(ev, CancellationToken.None);
        }
    }

    private static async Task<List<AutomationRuleExecution>> ExecutionsAsync(Harness harness)
    {
        await using ApplicationDbContext ctx = new(harness.Options);
        return await ctx.AutomationRuleExecutions.OrderBy(e => e.ExecutedAt).ToListAsync();
    }

    [TestMethod]
    public async Task TimeRemaining_FiresOnceWhenTheCountdownPassesTheThreshold()
    {
        Harness harness = await CreateHarnessAsync(Rule(AutomationTriggerType.TimeRemainingBelow, 60));

        await PublishAsync(harness, Status(GameStatus.InGame, 120));
        await PublishAsync(harness, Status(GameStatus.InGame, 61));
        Assert.IsEmpty(harness.Executor.ExecutedActionIds);

        await PublishAsync(harness, Status(GameStatus.InGame, 59));
        await PublishAsync(harness, Status(GameStatus.InGame, 30));
        await PublishAsync(harness, Status(GameStatus.InGame, 30));

        Assert.HasCount(1, harness.Executor.ExecutedActionIds, "Once per game, however often the kiosk republishes.");
        Assert.AreEqual(nameof(AutomationTriggerType.TimeRemainingBelow), (await ExecutionsAsync(harness)).Single().TriggerEvent);
    }

    [TestMethod]
    public async Task TimeRemaining_FiresAgainInTheNextGame()
    {
        Harness harness = await CreateHarnessAsync(Rule(AutomationTriggerType.TimeRemainingBelow, 60));

        await PublishAsync(harness, Status(GameStatus.InGame, 120));
        await PublishAsync(harness, Status(GameStatus.InGame, 50));
        await PublishAsync(harness, Status(GameStatus.NotStarted, 0));
        await PublishAsync(harness, Status(GameStatus.InGame, 300));
        await PublishAsync(harness, Status(GameStatus.InGame, 45));

        Assert.HasCount(2, harness.Executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task TimeRemaining_IgnoresTheZeroAKioskReportsBeforeItsFirstCountdownPacket()
    {
        Harness harness = await CreateHarnessAsync(Rule(AutomationTriggerType.TimeRemainingBelow, 60));

        await PublishAsync(harness, Status(GameStatus.InGame, 0));
        await PublishAsync(harness, Status(GameStatus.InGame, 30));

        Assert.IsEmpty(harness.Executor.ExecutedActionIds, "Nothing above the threshold was seen this game, so nothing was crossed.");
    }

    [TestMethod]
    public async Task TimeRemaining_DoesNotFireWhenTheGameEnds()
    {
        Harness harness = await CreateHarnessAsync(Rule(AutomationTriggerType.TimeRemainingBelow, 60));

        await PublishAsync(harness, Status(GameStatus.InGame, 120));
        await PublishAsync(harness, Status(GameStatus.NotStarted, 0));

        Assert.IsEmpty(harness.Executor.ExecutedActionIds, "The countdown zeroing at the end of a game isn't a crossing.");
    }

    [TestMethod]
    public async Task Score_AnyPlayer_FiresOnceWhenAPlayerReachesIt()
    {
        Harness harness = await CreateHarnessAsync(Rule(AutomationTriggerType.ScoreThreshold, 1000, AutomationScoreScope.AnyPlayer));

        await PublishAsync(harness, Status(GameStatus.InGame, 300, (1, Team.Red, 400), (2, Team.Green, 900)));
        Assert.IsEmpty(harness.Executor.ExecutedActionIds);

        await PublishAsync(harness, Status(GameStatus.InGame, 290, (1, Team.Red, 400), (2, Team.Green, 1200)));
        await PublishAsync(harness, Status(GameStatus.InGame, 280, (1, Team.Red, 1500), (2, Team.Green, 1300)));

        Assert.HasCount(1, harness.Executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task Score_DoesNotFireOnTheFinishedGamesScoresRepublishedAfterwards()
    {
        Harness harness = await CreateHarnessAsync(Rule(AutomationTriggerType.ScoreThreshold, 1000, AutomationScoreScope.AnyPlayer));

        await PublishAsync(harness, Status(GameStatus.NotStarted, 0, (1, Team.Red, 5000)));

        Assert.IsEmpty(harness.Executor.ExecutedActionIds);
    }

    [TestMethod]
    public void IsScoreThresholdReached_AddsUpATeamsPlayers()
    {
        List<PlayerScoreSnapshot> players = [new(1, Team.Red, 600), new(2, Team.Red, 500), new(3, Team.Green, 900), new(4, null, 5000)];

        Assert.IsTrue(AutomationEngineService.IsScoreThresholdReached(AutomationScoreScope.Team, null, 1000, players), "Red's 600 + 500 reaches 1000.");
        Assert.IsTrue(AutomationEngineService.IsScoreThresholdReached(AutomationScoreScope.Team, Team.Red, 1100, players));
        Assert.IsFalse(AutomationEngineService.IsScoreThresholdReached(AutomationScoreScope.Team, Team.Green, 1000, players));
        Assert.IsFalse(AutomationEngineService.IsScoreThresholdReached(AutomationScoreScope.Team, null, 1200, players), "A player with no team doesn't count towards any team.");
    }

    [TestMethod]
    public void IsScoreThresholdReached_CanLimitAnyPlayerToOneTeam()
    {
        List<PlayerScoreSnapshot> players = [new(1, Team.Red, 1500), new(2, Team.Green, 900)];

        Assert.IsTrue(AutomationEngineService.IsScoreThresholdReached(AutomationScoreScope.AnyPlayer, null, 1000, players));
        Assert.IsTrue(AutomationEngineService.IsScoreThresholdReached(AutomationScoreScope.AnyPlayer, Team.Red, 1000, players));
        Assert.IsFalse(AutomationEngineService.IsScoreThresholdReached(AutomationScoreScope.AnyPlayer, Team.Green, 1000, players));
    }

    [TestMethod]
    public async Task GameData_RespectsConditions_AndASkipUsesUpTheGame()
    {
        AutomationRuleCondition never = new()
        {
            Id = Guid.NewGuid(),
            ConditionType = AutomationConditionType.DaysOfWeek,
            ParametersJson = AutomationConditionParameters.Serialize(new DaysOfWeekConditionParameters(
                AutomationScheduleDays.Serialize([(DayOfWeek)(((int)DateTime.UtcNow.AddDays(3).DayOfWeek))]))),
            IsActive = true
        };
        Harness harness = await CreateHarnessAsync(Rule(AutomationTriggerType.ScoreThreshold, 100, AutomationScoreScope.AnyPlayer, null, never));

        await PublishAsync(harness, Status(GameStatus.InGame, 300, (1, Team.Red, 150)));
        await PublishAsync(harness, Status(GameStatus.InGame, 290, (1, Team.Red, 200)));

        Assert.IsEmpty(harness.Executor.ExecutedActionIds);
        AutomationRuleExecution skipped = (await ExecutionsAsync(harness)).Single();
        Assert.IsNotNull(skipped.SkipReason, "One skip row for the crossing, not one per publish.");
    }

    [TestMethod]
    public async Task EnqueueGameData_CoalescesToOnePendingEventPerKiosk()
    {
        Harness harness = await CreateHarnessAsync(Rule(AutomationTriggerType.TimeRemainingBelow, 60));

        for (int i = 0; i < 1000; i++)
        {
            harness.Engine.EnqueueGameData(KioskId, Status(GameStatus.InGame, 1000 - i));
        }

        Assert.AreEqual(1, harness.Engine.GameDataReader.Count, "A burst of publishes leaves one event waiting, carrying the latest snapshot.");
    }

    [TestMethod]
    public async Task EnqueueGameData_DoesNothingWhenNoRuleUsesGameData()
    {
        Harness harness = await CreateHarnessAsync(new AutomationRule
        {
            Id = Guid.NewGuid(),
            Name = "Status rule",
            TriggerClientId = KioskId,
            TriggerType = AutomationTriggerType.GameStatusTransition,
            TriggerEvent = GameStatus.InGame,
            IsActive = true,
            IsEnabled = true,
            CreateDate = DateTime.UtcNow
        });

        // The first publish lets the engine load its rules and learn there are no game-data ones.
        await PublishAsync(harness, Status(GameStatus.InGame, 100));

        harness.Engine.EnqueueGameData(KioskId, Status(GameStatus.InGame, 90));

        Assert.AreEqual(0, harness.Engine.GameDataReader.Count, "Venues without game-data rules get no extra work.");
    }

    [TestMethod]
    public async Task ExistingStatusRules_DoNotFireOnGameData_AndGameDataRulesDoNotFireOnStatusChanges()
    {
        AutomationRule statusRule = new()
        {
            Id = Guid.NewGuid(),
            Name = "Status rule",
            TriggerClientId = KioskId,
            TriggerType = AutomationTriggerType.GameStatusTransition,
            TriggerEvent = GameStatus.InGame,
            IsActive = true,
            IsEnabled = true,
            CreateDate = DateTime.UtcNow,
            Actions = [new AutomationRuleAction { Id = Guid.NewGuid(), ActionType = RecordingActionExecutor.TestActionType, IsActive = true }]
        };
        Harness harness = await CreateHarnessAsync(statusRule, Rule(AutomationTriggerType.ScoreThreshold, 100, AutomationScoreScope.AnyPlayer));

        // Game data alone (no transition processed) runs only the score rule.
        await PublishAsync(harness, Status(GameStatus.InGame, 300, (1, Team.Red, 500)));
        Assert.HasCount(1, harness.Executor.ExecutedActionIds);

        // A status edge alone runs only the status rule.
        await harness.Engine.ProcessTransitionAsync(new GameStatusTransitionEvent(KioskId, GameStatus.NotStarted, GameStatus.InGame), CancellationToken.None);
        Assert.HasCount(2, harness.Executor.ExecutedActionIds);
        Assert.AreEqual(statusRule.Actions[0].Id, harness.Executor.ExecutedActionIds[1]);
    }

    private static AutomationRule GameEndedRule(Team? winningTeam, params AutomationRuleCondition[] conditions)
    {
        AutomationRule rule = Rule(AutomationTriggerType.GameEnded, 0, null, winningTeam, conditions);
        rule.TriggerThreshold = null;
        rule.Name = winningTeam is Team team ? $"{team} wins" : "Anyone wins";
        return rule;
    }

    // A game played and ended through the transition channel, the way the hub reports it.
    private static async Task PlayToTheEndAsync(Harness harness, params (int Gun, Team? Team, int Score)[] finalScores)
    {
        harness.Engine.EnqueueTransition(KioskId, GameStatus.InGame, Status(GameStatus.InGame, 300));
        harness.Engine.EnqueueTransition(KioskId, GameStatus.NotStarted, Status(GameStatus.NotStarted, 0, finalScores));

        while (harness.Engine.Reader.TryRead(out GameStatusTransitionEvent? ev))
        {
            await harness.Engine.ProcessTransitionAsync(ev, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task GameEnded_FiresForTheWinningTeamsRuleOnly()
    {
        AutomationRule red = GameEndedRule(Team.Red);
        AutomationRule green = GameEndedRule(Team.Green);
        AutomationRule anyone = GameEndedRule(null);
        Harness harness = await CreateHarnessAsync(red, green, anyone);

        await PlayToTheEndAsync(harness, (1, Team.Red, 4000), (2, Team.Green, 1500));

        CollectionAssert.AreEquivalent(
            new[] { red.Actions[0].Id, anyone.Actions[0].Id },
            harness.Executor.ExecutedActionIds,
            "Red won, so the Red and any-team rules run and the Green one doesn't.");
        Assert.IsTrue((await ExecutionsAsync(harness)).All(e => e.TriggerEvent == "GameEnded (Red won)"));
    }

    [TestMethod]
    public async Task GameEnded_DoesNotFireOnADraw_OrASoloGame()
    {
        Harness harness = await CreateHarnessAsync(GameEndedRule(null));

        await PlayToTheEndAsync(harness, (1, Team.Red, 1000), (2, Team.Green, 1000));
        await PlayToTheEndAsync(harness, (1, null, 3000), (2, null, 1000));

        Assert.IsEmpty(harness.Executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task GameEnded_FiresOncePerGame_NotOnTheFinishedScoresRepublishedAfterwards()
    {
        Harness harness = await CreateHarnessAsync(GameEndedRule(Team.Red));

        await PlayToTheEndAsync(harness, (1, Team.Red, 4000), (2, Team.Green, 1500));

        // The kiosk keeps publishing NotStarted with the final scores until the next game.
        harness.Engine.EnqueueTransition(KioskId, GameStatus.NotStarted, Status(GameStatus.NotStarted, 0, (1, Team.Red, 4000), (2, Team.Green, 1500)));
        Assert.IsFalse(harness.Engine.Reader.TryRead(out _), "A repeated status is not a new edge.");

        Assert.HasCount(1, harness.Executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task GameEnded_RespectsConditions()
    {
        AutomationRuleCondition never = new()
        {
            Id = Guid.NewGuid(),
            ConditionType = AutomationConditionType.DaysOfWeek,
            ParametersJson = AutomationConditionParameters.Serialize(new DaysOfWeekConditionParameters(
                AutomationScheduleDays.Serialize([DateTime.UtcNow.AddDays(3).DayOfWeek]))),
            IsActive = true
        };
        Harness harness = await CreateHarnessAsync(GameEndedRule(Team.Red, never));

        await PlayToTheEndAsync(harness, (1, Team.Red, 4000), (2, Team.Green, 1500));

        Assert.IsEmpty(harness.Executor.ExecutedActionIds);
        Assert.AreEqual("GameEnded (Red won)", (await ExecutionsAsync(harness)).Single().TriggerEvent);
    }

    [TestMethod]
    public async Task GameEnded_IsIgnoredWhenTheCallerPassesNoScores()
    {
        // Older call sites (and tests) pass only the status; the existing status rules still run.
        Harness harness = await CreateHarnessAsync(GameEndedRule(null));

        harness.Engine.EnqueueTransition(KioskId, GameStatus.InGame);
        harness.Engine.EnqueueTransition(KioskId, GameStatus.NotStarted);

        while (harness.Engine.Reader.TryRead(out GameStatusTransitionEvent? ev))
        {
            Assert.IsNull(ev.FinalSnapshot);
            await harness.Engine.ProcessTransitionAsync(ev, CancellationToken.None);
        }

        Assert.IsEmpty(harness.Executor.ExecutedActionIds);
    }
}


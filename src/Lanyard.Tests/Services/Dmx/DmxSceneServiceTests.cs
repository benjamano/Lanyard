using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Dmx;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Dmx;

[TestClass]
public class DmxSceneServiceTests
{
    private const int MyCompany = 1;
    private const int OtherCompany = 2;

    private sealed class Harness
    {
        public DbContextOptions<ApplicationDbContext> Options { get; } = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        public Mock<IDmxSceneRunnerService> Runner { get; } = new();

        public DmxSceneService Service(int companyId = MyCompany)
        {
            Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
            factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ApplicationDbContext(Options, new FixedTenantProvider(companyId)));

            Mock<ISecurityService> security = new();
            security.Setup(s => s.GetCurrentUserIdAsync()).ReturnsAsync(Result<string>.Ok("user-1"));

            Runner.Setup(r => r.GetRunningSceneIds(It.IsAny<Guid>())).Returns([]);

            return new DmxSceneService(factory.Object, NullLogger<DmxSceneService>.Instance, Runner.Object, security.Object);
        }

        public ApplicationDbContext System() => new(Options);

        public async Task<Guid> SeedClientAsync(int companyId = MyCompany)
        {
            await using ApplicationDbContext ctx = System();
            Client client = new() { Id = Guid.NewGuid(), CompanyId = companyId, Name = "Kiosk", CreateDate = DateTime.UtcNow };
            ctx.Clients.Add(client);
            await ctx.SaveChangesAsync();
            return client.Id;
        }

        // A scene with one step per entry in stepNumbers.
        public async Task<(Guid sceneId, List<Guid> stepIds)> SeedSceneAsync(Guid clientId, int companyId = MyCompany, string name = "Scene", params int[] stepNumbers)
        {
            await using ApplicationDbContext ctx = System();
            DmxScene scene = new() { Id = Guid.NewGuid(), CompanyId = companyId, ClientId = clientId, Name = name, IsActive = true, CreateByUserId = "seed", CreateDate = DateTime.UtcNow };
            ctx.DmxScenes.Add(scene);

            List<Guid> stepIds = [];

            foreach (int number in stepNumbers)
            {
                DmxSceneStep step = new() { Id = Guid.NewGuid(), SceneId = scene.Id, StepNumber = number, Name = $"Step {number}", Duration = TimeSpan.FromSeconds(1), CreateByUserId = "seed" };
                ctx.DmxSceneSteps.Add(step);
                stepIds.Add(step.Id);
            }

            await ctx.SaveChangesAsync();
            return (scene.Id, stepIds);
        }
    }

    private static List<DmxChannel> Batch(params (int address, byte value)[] channels) =>
        channels.Select(c => new DmxChannel { Address = c.address, Value = c.value }).ToList();

    [TestMethod]
    public async Task SaveSceneStepChannelValuesAsync_UpsertsTheWholeBatch_OneRowPerChannel()
    {
        Harness harness = new();
        Guid clientId = await harness.SeedClientAsync();
        (Guid sceneId, List<Guid> steps) = await harness.SeedSceneAsync(clientId, stepNumbers: 1);

        DmxSceneService service = harness.Service();

        Result<bool> first = await service.SaveSceneStepChannelValuesAsync(steps[0], Batch((1, 10), (2, 20), (1, 11)));
        Result<bool> second = await service.SaveSceneStepChannelValuesAsync(steps[0], Batch((2, 22), (3, 30)));

        Assert.IsTrue(first.IsSuccess, first.Error);
        Assert.IsTrue(second.IsSuccess, second.Error);

        await using ApplicationDbContext ctx = harness.System();
        Dictionary<int, byte> rows = await ctx.DmxSceneStepChannelValues
            .Where(v => v.SceneStepId == steps[0])
            .ToDictionaryAsync(v => v.ChannelNumber, v => v.Value);

        Assert.AreEqual(3, rows.Count);
        Assert.AreEqual((byte)11, rows[1], "Latest value in a batch wins.");
        Assert.AreEqual((byte)22, rows[2]);
        Assert.AreEqual((byte)30, rows[3]);

        // An edit means a running copy of the scene is stale.
        harness.Runner.Verify(r => r.StopScene(sceneId), Times.Exactly(2));
    }

    [TestMethod]
    public async Task SaveSceneStepChannelValuesAsync_AnotherCompanysStep_IsRefusedAndNothingIsWritten()
    {
        Harness harness = new();
        Guid otherClient = await harness.SeedClientAsync(OtherCompany);
        (Guid sceneId, List<Guid> steps) = await harness.SeedSceneAsync(otherClient, OtherCompany, stepNumbers: 1);

        Result<bool> result = await harness.Service(MyCompany).SaveSceneStepChannelValuesAsync(steps[0], Batch((1, 255)));

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext ctx = harness.System();
        Assert.AreEqual(0, await ctx.DmxSceneStepChannelValues.CountAsync());
        harness.Runner.Verify(r => r.StopScene(sceneId), Times.Never);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(513)]
    public async Task SaveSceneStepChannelValuesAsync_ChannelOutOfRange_IsRefused(int channel)
    {
        Harness harness = new();
        Guid clientId = await harness.SeedClientAsync();
        (_, List<Guid> steps) = await harness.SeedSceneAsync(clientId, stepNumbers: 1);

        Result<bool> result = await harness.Service().SaveSceneStepChannelValuesAsync(steps[0], Batch((channel, 1)));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task DeleteSceneStepAsync_ClosesTheGap_SoStepsStayNumberedOneToN()
    {
        Harness harness = new();
        Guid clientId = await harness.SeedClientAsync();
        (Guid sceneId, List<Guid> steps) = await harness.SeedSceneAsync(clientId, stepNumbers: [1, 2, 3, 4]);

        Result<bool> result = await harness.Service().DeleteSceneStepAsync(steps[1]);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = harness.System();
        List<(Guid Id, int Number)> remaining = (await ctx.DmxSceneSteps
            .Where(s => s.SceneId == sceneId)
            .OrderBy(s => s.StepNumber)
            .ToListAsync())
            .Select(s => (s.Id, s.StepNumber))
            .ToList();

        CollectionAssert.AreEqual(new[] { steps[0], steps[2], steps[3] }, remaining.Select(r => r.Id).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, remaining.Select(r => r.Number).ToArray());
        harness.Runner.Verify(r => r.StopScene(sceneId), Times.Once);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(3601)]
    public async Task UpdateSceneStepDurationAsync_OutOfRange_IsRefused(int seconds)
    {
        Harness harness = new();
        Guid clientId = await harness.SeedClientAsync();
        (_, List<Guid> steps) = await harness.SeedSceneAsync(clientId, stepNumbers: 1);

        Result<bool> result = await harness.Service().UpdateSceneStepDurationAsync(steps[0], TimeSpan.FromSeconds(seconds));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task UpdateSceneStepDurationAsync_KeepsMilliseconds_AndStopsTheRunningScene()
    {
        Harness harness = new();
        Guid clientId = await harness.SeedClientAsync();
        (Guid sceneId, List<Guid> steps) = await harness.SeedSceneAsync(clientId, stepNumbers: 1);

        Result<bool> result = await harness.Service().UpdateSceneStepDurationAsync(steps[0], TimeSpan.FromMilliseconds(250));

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = harness.System();
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), (await ctx.DmxSceneSteps.SingleAsync()).Duration);
        harness.Runner.Verify(r => r.StopScene(sceneId), Times.Once);
    }

    [TestMethod]
    public async Task GetScenesForClientAsync_IsSortedByName()
    {
        Harness harness = new();
        Guid clientId = await harness.SeedClientAsync();
        await harness.SeedSceneAsync(clientId, name: "Strobe");
        await harness.SeedSceneAsync(clientId, name: "Ambient");
        await harness.SeedSceneAsync(clientId, name: "Chase");

        Result<IEnumerable<DmxSceneDTO>> result = await harness.Service().GetScenesForClientAsync(clientId);

        CollectionAssert.AreEqual(new[] { "Ambient", "Chase", "Strobe" }, result.Data!.Select(s => s.Name).ToArray());
    }

    [TestMethod]
    public async Task CreateSceneAsync_OnAnotherCompanysKiosk_IsRefused()
    {
        Harness harness = new();
        Guid otherClient = await harness.SeedClientAsync(OtherCompany);

        Result<DmxScene> result = await harness.Service(MyCompany).CreateSceneAsync(otherClient, "Mine now");

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext ctx = harness.System();
        Assert.AreEqual(0, await ctx.DmxScenes.CountAsync());
    }

    [TestMethod]
    public async Task DeleteSceneAsync_AnotherCompanysScene_IsRefusedAndNotStopped()
    {
        Harness harness = new();
        Guid otherClient = await harness.SeedClientAsync(OtherCompany);
        (Guid sceneId, _) = await harness.SeedSceneAsync(otherClient, OtherCompany);

        Result<bool> result = await harness.Service(MyCompany).DeleteSceneAsync(sceneId);

        Assert.IsFalse(result.IsSuccess);
        harness.Runner.Verify(r => r.StopScene(It.IsAny<Guid>()), Times.Never);
    }

    [TestMethod]
    public async Task UpdateSceneAsync_PlaybackFlagChange_StopsTheScene_RenameDoesNot()
    {
        Harness harness = new();
        Guid clientId = await harness.SeedClientAsync();
        (Guid sceneId, _) = await harness.SeedSceneAsync(clientId);
        DmxSceneService service = harness.Service();

        await service.UpdateSceneAsync(new DmxScene { Id = sceneId, ClientId = clientId, Name = "Renamed", Loop = true, CreateByUserId = "x" });
        harness.Runner.Verify(r => r.StopScene(sceneId), Times.Never);

        await service.UpdateSceneAsync(new DmxScene { Id = sceneId, ClientId = clientId, Name = "Renamed", Loop = false, CreateByUserId = "x" });
        harness.Runner.Verify(r => r.StopScene(sceneId), Times.Once);

        await using ApplicationDbContext ctx = harness.System();
        DmxScene saved = await ctx.DmxScenes.SingleAsync();
        Assert.AreEqual("Renamed", saved.Name);
        Assert.AreEqual("user-1", saved.UpdateByUserId);
        Assert.IsNotNull(saved.UpdateDate);
    }

    [TestMethod]
    public async Task UpdateSceneAsync_BlankName_IsRefused()
    {
        Harness harness = new();
        Guid clientId = await harness.SeedClientAsync();
        (Guid sceneId, _) = await harness.SeedSceneAsync(clientId);

        Result<bool> result = await harness.Service().UpdateSceneAsync(new DmxScene { Id = sceneId, Name = "  ", CreateByUserId = "x" });

        Assert.IsFalse(result.IsSuccess);
    }

    private static async Task<List<(Guid Id, int Number, string Name)>> StepsOfAsync(Harness harness, Guid sceneId)
    {
        await using ApplicationDbContext ctx = harness.System();

        return (await ctx.DmxSceneSteps.Where(s => s.SceneId == sceneId).OrderBy(s => s.StepNumber).ToListAsync())
            .Select(s => (s.Id, s.StepNumber, s.Name))
            .ToList();
    }

    [TestMethod]
    public async Task DuplicateStepAsync_CopiesValuesToJustAfterItAndShiftsTheRest()
    {
        Harness harness = new();
        Guid clientId = await harness.SeedClientAsync();
        (Guid sceneId, List<Guid> steps) = await harness.SeedSceneAsync(clientId, stepNumbers: [1, 2]);
        DmxSceneService service = harness.Service();
        await service.SaveSceneStepChannelValuesAsync(steps[0], Batch((5, 50), (6, 60)));

        Result<DmxSceneStep> copy = await service.DuplicateStepAsync(steps[0]);

        Assert.IsTrue(copy.IsSuccess, copy.Error);
        List<(Guid Id, int Number, string Name)> after = await StepsOfAsync(harness, sceneId);
        CollectionAssert.AreEqual(new[] { steps[0], copy.Data!.Id, steps[1] }, after.Select(s => s.Id).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, after.Select(s => s.Number).ToArray());
        Assert.AreEqual("Step 1 copy", after[1].Name);

        await using ApplicationDbContext ctx = harness.System();
        Assert.AreEqual(2, await ctx.DmxSceneStepChannelValues.CountAsync(v => v.SceneStepId == copy.Data.Id));
    }

    [TestMethod]
    public async Task ReorderStepsAsync_RenumbersInTheGivenOrder()
    {
        Harness harness = new();
        Guid clientId = await harness.SeedClientAsync();
        (Guid sceneId, List<Guid> steps) = await harness.SeedSceneAsync(clientId, stepNumbers: [1, 2, 3]);

        Result<bool> result = await harness.Service().ReorderStepsAsync(sceneId, [steps[2], steps[0], steps[1]]);

        Assert.IsTrue(result.IsSuccess, result.Error);
        CollectionAssert.AreEqual(new[] { steps[2], steps[0], steps[1] }, (await StepsOfAsync(harness, sceneId)).Select(s => s.Id).ToArray());
        harness.Runner.Verify(r => r.StopScene(sceneId), Times.Once);
    }

    [TestMethod]
    public async Task ReorderStepsAsync_AStaleList_IsRefusedAndNothingMoves()
    {
        Harness harness = new();
        Guid clientId = await harness.SeedClientAsync();
        (Guid sceneId, List<Guid> steps) = await harness.SeedSceneAsync(clientId, stepNumbers: [1, 2, 3]);

        Result<bool> missing = await harness.Service().ReorderStepsAsync(sceneId, [steps[1], steps[0]]);
        Result<bool> repeated = await harness.Service().ReorderStepsAsync(sceneId, [steps[1], steps[1], steps[0]]);

        Assert.IsFalse(missing.IsSuccess);
        Assert.IsFalse(repeated.IsSuccess);
        CollectionAssert.AreEqual(steps.ToArray(), (await StepsOfAsync(harness, sceneId)).Select(s => s.Id).ToArray());
    }

    [TestMethod]
    public async Task ReplaceStepChannelValuesAsync_LeavesExactlyTheCapturedChannels()
    {
        Harness harness = new();
        Guid clientId = await harness.SeedClientAsync();
        (_, List<Guid> steps) = await harness.SeedSceneAsync(clientId, stepNumbers: 1);
        DmxSceneService service = harness.Service();
        await service.SaveSceneStepChannelValuesAsync(steps[0], Batch((1, 1), (2, 2)));

        Result<bool> result = await service.ReplaceStepChannelValuesAsync(steps[0], Batch((2, 20), (3, 30)));

        Assert.IsTrue(result.IsSuccess, result.Error);
        await using ApplicationDbContext ctx = harness.System();
        Dictionary<int, byte> values = await ctx.DmxSceneStepChannelValues.Where(v => v.SceneStepId == steps[0]).ToDictionaryAsync(v => v.ChannelNumber, v => v.Value);
        CollectionAssert.AreEquivalent(new[] { 2, 3 }, values.Keys.ToArray());
        Assert.AreEqual((byte)20, values[2]);
    }

    [TestMethod]
    public async Task ClearStepChannelAsync_RemovesTheChannelNotJustZeroesIt()
    {
        Harness harness = new();
        Guid clientId = await harness.SeedClientAsync();
        (Guid sceneId, List<Guid> steps) = await harness.SeedSceneAsync(clientId, stepNumbers: 1);
        DmxSceneService service = harness.Service();
        await service.SaveSceneStepChannelValuesAsync(steps[0], Batch((4, 99)));

        Result<bool> result = await service.ClearStepChannelAsync(steps[0], 4);

        Assert.IsTrue(result.IsSuccess, result.Error);
        await using ApplicationDbContext ctx = harness.System();
        Assert.AreEqual(0, await ctx.DmxSceneStepChannelValues.CountAsync());
    }

    [TestMethod]
    public async Task ClearStepChannelAsync_AnotherCompanysStep_LeavesItAlone()
    {
        Harness harness = new();
        Guid theirClient = await harness.SeedClientAsync(OtherCompany);
        (_, List<Guid> steps) = await harness.SeedSceneAsync(theirClient, OtherCompany, stepNumbers: 1);
        await harness.Service(OtherCompany).SaveSceneStepChannelValuesAsync(steps[0], Batch((4, 99)));

        await harness.Service(MyCompany).ClearStepChannelAsync(steps[0], 4);

        await using ApplicationDbContext ctx = harness.System();
        Assert.AreEqual(1, await ctx.DmxSceneStepChannelValues.CountAsync());
    }

    [TestMethod]
    public async Task RenameStepAsync_TrimsAndRefusesBlank()
    {
        Harness harness = new();
        Guid clientId = await harness.SeedClientAsync();
        (Guid sceneId, List<Guid> steps) = await harness.SeedSceneAsync(clientId, stepNumbers: 1);
        DmxSceneService service = harness.Service();

        Assert.IsFalse((await service.RenameStepAsync(steps[0], "   ")).IsSuccess);
        Assert.IsTrue((await service.RenameStepAsync(steps[0], "  Red hit ")).IsSuccess);

        Assert.AreEqual("Red hit", (await StepsOfAsync(harness, sceneId))[0].Name);
        harness.Runner.Verify(r => r.StopScene(It.IsAny<Guid>()), Times.Never);
    }

    [TestMethod]
    public async Task GetAutomationRulesUsingSceneAsync_FindsRulesWhoseDmxActionRunsIt()
    {
        Harness harness = new();
        Guid clientId = await harness.SeedClientAsync();
        (Guid sceneId, _) = await harness.SeedSceneAsync(clientId);
        (Guid otherScene, _) = await harness.SeedSceneAsync(clientId, name: "Other");

        await using (ApplicationDbContext ctx = harness.System())
        {
            AutomationRule uses = new() { Id = Guid.NewGuid(), CompanyId = MyCompany, Name = "Winner lights", TriggerClientId = clientId, IsActive = true };
            AutomationRule other = new() { Id = Guid.NewGuid(), CompanyId = MyCompany, Name = "Other rule", TriggerClientId = clientId, IsActive = true };
            ctx.AutomationRules.AddRange(uses, other);
            ctx.AutomationRuleActions.AddRange(
                new AutomationRuleAction { Id = Guid.NewGuid(), AutomationRuleId = uses.Id, ActionType = AutomationActionTypes.DmxSceneControl, IsActive = true,
                    ParametersJson = $"{{\"TargetClientId\":\"{clientId}\",\"Operation\":\"StartScene\",\"SceneId\":\"{sceneId}\"}}" },
                new AutomationRuleAction { Id = Guid.NewGuid(), AutomationRuleId = other.Id, ActionType = AutomationActionTypes.DmxSceneControl, IsActive = true,
                    ParametersJson = $"{{\"TargetClientId\":\"{clientId}\",\"Operation\":\"StartScene\",\"SceneId\":\"{otherScene}\"}}" });
            await ctx.SaveChangesAsync();
        }

        Result<IReadOnlyList<string>> result = await harness.Service().GetAutomationRulesUsingSceneAsync(sceneId);

        CollectionAssert.AreEqual(new[] { "Winner lights" }, result.Data!.ToArray());
    }
}


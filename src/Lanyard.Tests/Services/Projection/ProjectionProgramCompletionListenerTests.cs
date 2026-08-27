using Lanyard.Application.Services;
using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Projection;

[TestClass]
public class ProjectionProgramCompletionListenerTests
{
    // A minimal runner stand-in: the listener only cares about the completion event, and
    // raising it by hand keeps this test off the real runner's background timing.
    private sealed class FakeProjectionProgramRunnerService : IProjectionProgramRunnerService
    {
        public event Action<Guid, int, Guid>? OnProgramStarted;
        public event Action<Guid, int, int>? OnProgramStepAdvanced;
        public event Action<Guid, int, bool>? OnProgramPauseChanged;
        public event Action<Guid, int, Guid>? OnProgramStopped;
        public event Action<Guid, int, Guid>? OnProgramCompletedNaturally;

        public bool HasCompletionSubscriber => OnProgramCompletedNaturally is not null;

        public void RaiseCompletedNaturally(Guid clientId, int displayIndex, Guid programId)
        {
            OnProgramCompletedNaturally?.Invoke(clientId, displayIndex, programId);

            // Referenced so the compiler doesn't warn about the unused events this fake
            // has to declare to satisfy the interface.
            _ = OnProgramStarted;
            _ = OnProgramStepAdvanced;
            _ = OnProgramPauseChanged;
            _ = OnProgramStopped;
        }

        public Task<Result<Guid>> StartAsync(Guid clientId, int displayIndex, Guid programId, bool repeatInfinitely, int repeatCount, bool isTemporaryTrigger)
            => Task.FromResult(Result<Guid>.Ok(Guid.NewGuid()));

        public Result<bool> Pause(Guid clientId, int displayIndex) => Result<bool>.Ok(true);
        public Result<bool> Resume(Guid clientId, int displayIndex) => Result<bool>.Ok(true);
        public Result<bool> SkipToStep(Guid clientId, int displayIndex, int stepIndex) => Result<bool>.Ok(true);
        public Result<bool> SkipToNextStep(Guid clientId, int displayIndex) => Result<bool>.Ok(true);
        public Result<bool> SkipToPreviousStep(Guid clientId, int displayIndex) => Result<bool>.Ok(true);
        public Result<bool> Stop(Guid clientId, int displayIndex, Guid? runId = null) => Result<bool>.Ok(true);
        public ProjectionRunState? GetRunningState(Guid clientId, int displayIndex) => null;
        public List<ProjectionRunState> GetRunningStates() => [];
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    [TestMethod]
    public async Task CompletedNaturally_TellsTheClientToCloseItsTemporaryProjectionWindow()
    {
        Guid clientId = Guid.NewGuid();
        Guid programId = Guid.NewGuid();

        Mock<IClientService> clientServiceMock = new();
        clientServiceMock
            .Setup(c => c.CloseTemporaryProjectionWindowOnClientAsync(It.IsAny<Guid>(), It.IsAny<int>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        ServiceCollection services = new();
        services.AddScoped(_ => clientServiceMock.Object);

        await using ServiceProvider provider = services.BuildServiceProvider();

        FakeProjectionProgramRunnerService runner = new();

        ProjectionProgramCompletionListener listener = new(
            runner,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<ProjectionProgramCompletionListener>>());

        await listener.StartAsync(CancellationToken.None);

        runner.RaiseCompletedNaturally(clientId, 2, programId);

        Assert.IsTrue(await WaitUntilAsync(() =>
        {
            try
            {
                clientServiceMock.Verify(c => c.CloseTemporaryProjectionWindowOnClientAsync(clientId, 2), Times.Once);

                return true;
            }
            catch (MockException)
            {
                return false;
            }
        }));

        await listener.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task StopAsync_UnsubscribesFromTheRunner()
    {
        Mock<IClientService> clientServiceMock = new();
        clientServiceMock
            .Setup(c => c.CloseTemporaryProjectionWindowOnClientAsync(It.IsAny<Guid>(), It.IsAny<int>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        ServiceCollection services = new();
        services.AddScoped(_ => clientServiceMock.Object);

        await using ServiceProvider provider = services.BuildServiceProvider();

        FakeProjectionProgramRunnerService runner = new();

        ProjectionProgramCompletionListener listener = new(
            runner,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<ProjectionProgramCompletionListener>>());

        await listener.StartAsync(CancellationToken.None);
        Assert.IsTrue(runner.HasCompletionSubscriber);

        await listener.StopAsync(CancellationToken.None);
        Assert.IsFalse(runner.HasCompletionSubscriber);

        runner.RaiseCompletedNaturally(Guid.NewGuid(), 0, Guid.NewGuid());

        await Task.Delay(200);

        clientServiceMock.Verify(c => c.CloseTemporaryProjectionWindowOnClientAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
    }
}

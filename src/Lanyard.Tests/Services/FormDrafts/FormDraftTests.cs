using System.Text.Json;
using Lanyard.App.Services.FormDrafts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lanyard.Tests.Services.FormDrafts;

[TestClass]
public class FormDraftTests
{
    private sealed record TestDraft(Guid Id, string Name);

    private sealed class FakeStore(IDictionary<string, byte[]> state) : IPersistentComponentStateStore
    {
        public Task<IDictionary<string, byte[]>> GetPersistedStateAsync() => Task.FromResult(state);

        public Task PersistStateAsync(IReadOnlyDictionary<string, byte[]> state) => Task.CompletedTask;
    }

    private static async Task<PersistentComponentState> StateWithAsync(string key, object? value)
    {
        Dictionary<string, byte[]> saved = [];
        if (value is not null)
        {
            saved[$"lanyard-form-draft:{key}"] = JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web);
        }

        ComponentStatePersistenceManager manager = new(NullLogger<ComponentStatePersistenceManager>.Instance);
        await manager.RestoreStateAsync(new FakeStore(saved));
        return manager.State;
    }

    [TestMethod]
    public async Task TakeRestored_ReturnsTheSavedDraftOnce()
    {
        TestDraft saved = new(Guid.NewGuid(), "Ava Thompson");
        PersistentComponentState state = await StateWithAsync("party-editor", saved);

        using FormDraft<TestDraft> draft = new(state, "party-editor", () => null);

        Assert.AreEqual(saved, draft.TakeRestored());
        Assert.IsNull(draft.TakeRestored(), "A second call must not apply the draft again");
    }

    [TestMethod]
    public async Task TakeRestored_IsNull_WhenNothingWasSaved()
    {
        PersistentComponentState state = await StateWithAsync("party-editor", null);

        using FormDraft<TestDraft> draft = new(state, "party-editor", () => null);

        Assert.IsNull(draft.TakeRestored());
    }

    [TestMethod]
    public async Task TakeRestored_IgnoresAnotherPagesDraft()
    {
        PersistentComponentState state = await StateWithAsync("user-editor", new TestDraft(Guid.NewGuid(), "Someone"));

        using FormDraft<TestDraft> draft = new(state, "party-editor", () => null);

        Assert.IsNull(draft.TakeRestored());
    }
}

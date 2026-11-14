using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Lanyard.App.Services.FormDrafts;

// Keeps a form's unsaved input across a paused circuit (see CircuitShutdownCoordinator: on a
// deploy, open pages pause and then resume on the new server). On pause, the snapshot from
// `capture` is saved with the circuit's persisted state. On resume the page is a fresh component,
// so it calls TakeRestored() once its data has loaded and copies the snapshot back into its fields.
//
// Only covers a pause and resume, not a refresh: a reload starts a brand new circuit with nothing
// saved. Draft types must be plain JSON-serialisable records, never EF entities (cycles, and
// UserProfile carries password hashes). The key names the page; a page that edits a record should
// put the record's id in its draft and check it before applying, since the route can change under
// the same component.
public sealed class FormDraft<T> : IDisposable where T : class
{
    private readonly PersistentComponentState _state;
    private readonly string _key;
    private readonly PersistingComponentStateSubscription _subscription;
    private T? _restored;

    public FormDraft(PersistentComponentState state, string key, Func<T?> capture)
    {
        _state = state;
        _key = $"lanyard-form-draft:{key}";

        _state.TryTakeFromJson(_key, out _restored);

        // InteractiveServer only: there's nothing worth carrying from a prerender, and the
        // capture delegate may read fields that the prerender pass never filled in.
        _subscription = _state.RegisterOnPersisting(() =>
        {
            if (capture() is T draft)
            {
                _state.PersistAsJson(_key, draft);
            }

            return Task.CompletedTask;
        }, RenderMode.InteractiveServer);
    }

    // The draft saved when the circuit paused, if this page was resumed with one. Returns it once;
    // later calls return null, so a re-run of OnParametersSetAsync can't apply it twice.
    public T? TakeRestored()
    {
        T? restored = _restored;
        _restored = null;
        return restored;
    }

    public void Dispose() => _subscription.Dispose();
}

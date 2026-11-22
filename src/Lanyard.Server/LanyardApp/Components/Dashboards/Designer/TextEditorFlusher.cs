namespace Lanyard.App.Components.Dashboards.Designer;

// Text widget editors copy what's typed into the widget on focus-out and on a short poll
// (BlazoredTextEditor has no change event). Saving straight after typing - Ctrl+S with the caret
// still in the editor - can beat both, so the designer cascades one of these: every open editor
// registers itself, and the page flushes them all before it saves.
public sealed class TextEditorFlusher
{
    private readonly List<Func<Task>> _flushers = [];

    public IDisposable Register(Func<Task> flush)
    {
        _flushers.Add(flush);
        return new Registration(this, flush);
    }

    public async Task FlushAsync()
    {
        foreach (Func<Task> flush in _flushers.ToList())
        {
            await flush();
        }
    }

    private sealed class Registration(TextEditorFlusher owner, Func<Task> flush) : IDisposable
    {
        public void Dispose() => owner._flushers.Remove(flush);
    }
}

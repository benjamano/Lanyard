using System.Collections.Concurrent;

namespace Lanyard.Application.Services.Chat;

public enum ChatEventKind
{
    MessagePosted,
    MessageChanged,
    ConversationChanged,
    Read
}

// Who a chat change concerns: the conversation's current members. Pages ignore events that
// don't include their viewer.
public record ChatEvent(Guid ConversationId, IReadOnlyCollection<string> MemberUserIds, ChatEventKind Kind);

// Tells open chat pages and unread badges that something changed, in-process (the same
// single-app-instance assumption as the other event buses).
public interface IChatEventBus
{
    event Action<ChatEvent>? OnChanged;

    void Publish(ChatEvent chatEvent);
}

public class ChatEventBus : IChatEventBus
{
    public event Action<ChatEvent>? OnChanged;

    public void Publish(ChatEvent chatEvent) => OnChanged?.Invoke(chatEvent);
}

// Which conversations people have open right now, so someone reading a conversation isn't also
// sent a push for every message in it. Counted, because one person may have it open in two tabs.
public interface IChatPresence
{
    IDisposable Enter(string userId, Guid conversationId);

    bool IsViewing(string userId, Guid conversationId);
}

public class ChatPresence : IChatPresence
{
    private readonly ConcurrentDictionary<(string, Guid), int> _viewers = new();

    public IDisposable Enter(string userId, Guid conversationId)
    {
        (string, Guid) key = (userId, conversationId);
        _viewers.AddOrUpdate(key, 1, (_, count) => count + 1);

        return new Leave(() =>
        {
            if (_viewers.AddOrUpdate(key, 0, (_, count) => count - 1) <= 0)
            {
                _viewers.TryRemove(new KeyValuePair<(string, Guid), int>(key, 0));
            }
        });
    }

    public bool IsViewing(string userId, Guid conversationId) =>
        _viewers.TryGetValue((userId, conversationId), out int count) && count > 0;

    private sealed class Leave(Action leave) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                leave();
            }
        }
    }
}

// Who is typing in which conversation right now, for the "Tom is typing" line. The message box
// says so every few seconds while someone types; if that stops (they paused, closed the tab, lost
// signal) it lapses on its own after Lifetime. Only a change in who's typing is announced, not each
// refresh.
public interface IChatTyping
{
    event Action<Guid>? OnChanged;

    void Typing(string userId, Guid conversationId);

    void Stopped(string userId, Guid conversationId);

    IReadOnlyCollection<string> WhoIsTyping(Guid conversationId);
}

public class ChatTyping(TimeProvider time) : IChatTyping
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);

    private readonly TimeProvider _time = time;
    private readonly ConcurrentDictionary<(Guid, string), DateTimeOffset> _typing = new();

    public event Action<Guid>? OnChanged;

    public void Typing(string userId, Guid conversationId)
    {
        (Guid, string) key = (conversationId, userId);
        DateTimeOffset until = _time.GetUtcNow() + Lifetime;
        bool started = false;

        _typing.AddOrUpdate(key, _ => { started = true; return until; }, (_, _) => until);

        if (started)
        {
            OnChanged?.Invoke(conversationId);
            _ = LapseAsync(key);
        }
    }

    public void Stopped(string userId, Guid conversationId)
    {
        if (_typing.TryRemove((conversationId, userId), out _))
        {
            OnChanged?.Invoke(conversationId);
        }
    }

    public IReadOnlyCollection<string> WhoIsTyping(Guid conversationId)
    {
        DateTimeOffset now = _time.GetUtcNow();

        return _typing.Where(x => x.Key.Item1 == conversationId && x.Value > now).Select(x => x.Key.Item2).ToList();
    }

    // Waits until the entry's time is up (it moves on each refresh), then drops it.
    private async Task LapseAsync((Guid, string) key)
    {
        while (_typing.TryGetValue(key, out DateTimeOffset until))
        {
            TimeSpan left = until - _time.GetUtcNow();

            if (left <= TimeSpan.Zero)
            {
                if (_typing.TryRemove(new KeyValuePair<(Guid, string), DateTimeOffset>(key, until)))
                {
                    OnChanged?.Invoke(key.Item1);
                    return;
                }

                continue;
            }

            await Task.Delay(left, _time);
        }
    }
}

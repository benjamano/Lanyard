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

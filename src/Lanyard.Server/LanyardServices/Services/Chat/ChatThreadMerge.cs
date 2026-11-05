using Lanyard.Infrastructure.DTO.Chat;

namespace Lanyard.Application.Services.Chat;

// Adds newly fetched messages to the ones an open conversation already shows, in the order the
// database pages them: oldest first, and messages sent in the same instant by id. Guid.CompareTo
// orders ids as their text does, which is how Postgres orders uuids, so such messages don't swap
// over between a reload and an add.
public static class ChatThreadMerge
{
    public static int Compare(ChatMessageView a, ChatMessageView b)
    {
        int byTime = a.Message.CreateUtc.CompareTo(b.Message.CreateUtc);

        return byTime != 0 ? byTime : a.Message.Id.CompareTo(b.Message.Id);
    }

    // Every message from both, once: a fetched copy replaces the one already shown (it may have
    // been edited since), and anything new goes in its place by time.
    public static List<ChatMessageView> Merge(IReadOnlyList<ChatMessageView> existing, IReadOnlyList<ChatMessageView> fetched)
    {
        Dictionary<Guid, ChatMessageView> byId = existing.ToDictionary(x => x.Message.Id);

        foreach (ChatMessageView view in fetched)
        {
            byId[view.Message.Id] = view;
        }

        List<ChatMessageView> merged = [.. byId.Values];
        merged.Sort(Compare);

        return merged;
    }

    // The fetched messages that weren't already shown, oldest first.
    public static List<ChatMessageView> Added(IReadOnlyList<ChatMessageView> existing, IReadOnlyList<ChatMessageView> fetched)
    {
        HashSet<Guid> shown = [.. existing.Select(x => x.Message.Id)];

        return fetched.Where(x => !shown.Contains(x.Message.Id)).OrderBy(x => x, Comparer<ChatMessageView>.Create(Compare)).ToList();
    }
}

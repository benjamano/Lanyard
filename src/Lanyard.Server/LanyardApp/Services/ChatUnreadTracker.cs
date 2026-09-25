using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Chat;
using Lanyard.Infrastructure.DTO;

namespace Lanyard.App.Services;

// The signed-in person's unread chat count for this browser session (scoped = one per circuit),
// shared by the side nav and the phone's bottom bar so they don't each count. Recounts when a
// chat change involves them.
public sealed class ChatUnreadTracker(IChatService chatService, IChatEventBus eventBus, ISecurityService securityService) : IDisposable
{
    private readonly IChatService _chatService = chatService;
    private readonly IChatEventBus _eventBus = eventBus;
    private readonly ISecurityService _securityService = securityService;

    private string? _userId;
    private bool _started;

    public int Count { get; private set; }

    public event Action? Changed;

    public async Task StartAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        Result<string> user = await _securityService.GetCurrentUserIdAsync();

        if (!user.IsSuccess || string.IsNullOrEmpty(user.Data))
        {
            return;
        }

        _userId = user.Data;
        _eventBus.OnChanged += OnChatEvent;

        await RefreshAsync();
    }

    private void OnChatEvent(ChatEvent chatEvent)
    {
        if (_userId is null || (chatEvent.MemberUserIds.Count > 0 && !chatEvent.MemberUserIds.Contains(_userId)))
        {
            return;
        }

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_userId is null)
        {
            return;
        }

        Result<int> unread = await _chatService.GetUnreadTotalAsync(_userId);

        if (unread.IsSuccess && unread.Data != Count)
        {
            Count = unread.Data;
            Changed?.Invoke();
        }
    }

    public void Dispose() => _eventBus.OnChanged -= OnChatEvent;
}

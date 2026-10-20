namespace Lanyard.App.Components.Notifications;

// The install/notifications prompt and the star-rating prompt both sit in the same bottom corner.
// One per circuit, so they can see each other: whichever is up first keeps the corner, and the
// other waits for a later page.
public sealed class CornerPromptCoordinator
{
    private string? _holder;

    public bool TryTake(string who)
    {
        if (_holder is not null && _holder != who)
        {
            return false;
        }

        _holder = who;
        return true;
    }

    public void Release(string who)
    {
        if (_holder == who)
        {
            _holder = null;
        }
    }
}

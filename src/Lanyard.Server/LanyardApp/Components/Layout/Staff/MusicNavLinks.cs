namespace Lanyard.App.Components.Layout.Staff;

/// <summary>
/// Which links the side menu's Music group shows. Each link keeps the rule of the page it opens;
/// the group itself shows when at least one link does.
/// </summary>
public readonly record struct MusicNavLinks(bool Player, bool AudioClips, bool Schedule)
{
    public bool Any => Player || AudioClips || Schedule;

    public static MusicNavLinks For(bool canControlMusic, bool canPlayAudioClips, bool audioClipsEnabled, bool isManager) =>
        new(Player: canControlMusic,
            AudioClips: canPlayAudioClips && audioClipsEnabled,
            Schedule: isManager);
}

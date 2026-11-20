using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

/// <summary>
/// Tells anyone interested that a company's playlists changed - created, renamed, deleted, or
/// songs added, removed or moved - so open Music pages and the player's queues can catch up.
/// </summary>
public interface IMusicLibraryEvents
{
    /// <summary>
    /// (companyId, playlistId). The playlist is null when the change isn't about one playlist's
    /// songs (e.g. one was created or deleted, so the list itself changed).
    /// </summary>
    event Action<int, Guid?>? Changed;

    void Raise(int companyId, Guid? playlistId);
}

public sealed class MusicLibraryEvents(ILogger<MusicLibraryEvents> logger) : IMusicLibraryEvents
{
    public event Action<int, Guid?>? Changed;

    public void Raise(int companyId, Guid? playlistId)
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        // Each subscriber on its own, so one that throws can't stop the rest hearing about it.
        foreach (Action<int, Guid?> handler in handlers.GetInvocationList().Cast<Action<int, Guid?>>())
        {
            try
            {
                handler(companyId, playlistId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A music library change subscriber threw for company {CompanyId}, playlist {PlaylistId}", companyId, playlistId);
            }
        }
    }
}

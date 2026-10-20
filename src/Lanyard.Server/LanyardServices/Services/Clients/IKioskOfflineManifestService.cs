using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.DTO;

namespace Lanyard.Application.Services;

public interface IKioskOfflineManifestService
{
    /// <summary>
    /// What a kiosk should play from its own disk while it can't reach the server: each display's
    /// ambient program, reduced to the steps that can play offline, plus its offline settings.
    /// </summary>
    Task<Result<KioskOfflineManifestDTO>> BuildManifestAsync(Guid clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when <paramref name="fileId"/> is a video in that kiosk's current manifest. The offline
    /// media endpoint serves nothing else, so the kiosk secret can't be used to read arbitrary files.
    /// </summary>
    Task<Result<bool>> IsFileInManifestAsync(Guid clientId, Guid fileId, CancellationToken cancellationToken = default);
}

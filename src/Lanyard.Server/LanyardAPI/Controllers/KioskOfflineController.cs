using Lanyard.API.Extensions;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.Contracts;
using Lanyard.Shared.DTO;
using Microsoft.AspNetCore.Mvc;

namespace Lanyard.API.Controllers;

// Kiosk-facing: the kiosk downloads its offline manifest and the videos in it, so its screens can
// keep playing while the server is unreachable. Authenticated with the kiosk shared secret like
// the other kiosk endpoints. Read-only.
[ApiController]
public class KioskOfflineController(
    IKioskOfflineManifestService manifestService,
    IFileService fileService,
    IClientSecretValidator clientSecretValidator) : ControllerBase
{
    private readonly IKioskOfflineManifestService _manifestService = manifestService;
    private readonly IFileService _fileService = fileService;
    private readonly IClientSecretValidator _clientSecretValidator = clientSecretValidator;

    [HttpGet(KioskOfflineRoutes.ManifestTemplate)]
    public async Task<IActionResult> GetManifest(Guid clientId, CancellationToken cancellationToken)
    {
        if (!ClientRequestAuthorization.IsAuthorized(HttpContext, _clientSecretValidator))
        {
            return Unauthorized();
        }

        Result<KioskOfflineManifestDTO> result = await _manifestService.BuildManifestAsync(clientId, cancellationToken);

        return result.ToActionResult();
    }

    // Only serves a video that is in this kiosk's own manifest, so the secret can't be used to
    // read any other file. Every failure is a bare 404, so "not yours" and "doesn't exist" look the
    // same. Range requests are supported, which lets a kiosk resume a large download.
    [HttpGet(KioskOfflineRoutes.MediaTemplate)]
    public async Task<IActionResult> GetMedia(Guid clientId, Guid fileId, CancellationToken cancellationToken)
    {
        if (!ClientRequestAuthorization.IsAuthorized(HttpContext, _clientSecretValidator))
        {
            return Unauthorized();
        }

        Result<bool> referenced = await _manifestService.IsFileInManifestAsync(clientId, fileId, cancellationToken);

        if (!referenced.IsSuccess || !referenced.Data)
        {
            return NotFound();
        }

        Result<FileContent> content = await _fileService.OpenFileContentAsync(fileId, FileContentStreamingExtensions.ParseRequestedRange(Request), cancellationToken);

        if (!content.Success || content.Data is null)
        {
            return NotFound();
        }

        return await this.StreamFileContentAsync(content.Data, cacheFor: TimeSpan.FromDays(1));
    }
}

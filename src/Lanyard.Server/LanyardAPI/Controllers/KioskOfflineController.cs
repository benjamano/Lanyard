using Lanyard.API.Extensions;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.Contracts;
using Lanyard.Shared.DTO;
using Microsoft.AspNetCore.Mvc;

namespace Lanyard.API.Controllers;

// Kiosk-facing: the kiosk downloads its offline manifest and the videos in it, so its screens can
// keep playing while the server is unreachable. A kiosk may only read its own. Read-only.
[ApiController]
public class KioskOfflineController(
    IKioskOfflineManifestService manifestService,
    IFileService fileService) : ControllerBase
{
    private readonly IKioskOfflineManifestService _manifestService = manifestService;
    private readonly IFileService _fileService = fileService;

    [HttpGet(KioskOfflineRoutes.ManifestTemplate)]
    public async Task<IActionResult> GetManifest(Guid clientId, CancellationToken cancellationToken)
    {
        switch (await KioskRequestAuthorization.AuthorizeAsync(HttpContext, clientId))
        {
            case KioskRequestAccess.Denied:
                return Challenge(KioskAuthenticationDefaults.Scheme);
            case KioskRequestAccess.NotThisKiosk:
                return NotFound();
        }

        Result<KioskOfflineManifestDTO> result = await _manifestService.BuildManifestAsync(clientId, cancellationToken);

        return result.ToActionResult();
    }

    // Only serves a video that is in this kiosk's own manifest, so a kiosk can't use this to
    // read any other file. Every failure is a bare 404, so "not yours" and "doesn't exist" look the
    // same. Range requests are supported, which lets a kiosk resume a large download.
    [HttpGet(KioskOfflineRoutes.MediaTemplate)]
    public async Task<IActionResult> GetMedia(Guid clientId, Guid fileId, CancellationToken cancellationToken)
    {
        switch (await KioskRequestAuthorization.AuthorizeAsync(HttpContext, clientId))
        {
            case KioskRequestAccess.Denied:
                return Challenge(KioskAuthenticationDefaults.Scheme);
            case KioskRequestAccess.NotThisKiosk:
                return NotFound();
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

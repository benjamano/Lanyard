using Lanyard.API.Extensions;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.AudioClips;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.API.Controllers;

/// <summary>
/// Serves audio clips to kiosks, which download them before playing them over the music.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AudioClipsController(IDbContextFactory<ApplicationDbContext> factory, IFileService fileService, IClientSecretValidator clientSecretValidator) : ControllerBase
{
    // The kiosk shared secret, or a signed-in user, as for songs (MusicController.GetAudioFile).
    [HttpGet("{id:guid}/audio")]
    public async Task<IActionResult> GetAudio(Guid id, CancellationToken cancellationToken)
    {
        if (!ClientRequestAuthorization.IsAuthorized(HttpContext, clientSecretValidator))
        {
            return Unauthorized();
        }

        await using ApplicationDbContext ctx = await factory.CreateDbContextAsync(cancellationToken);

        var clip = await ctx.AudioClips
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.Id == id && x.DeleteDate == null)
            .Select(x => new { x.FileMetadataId, FileName = x.FileMetadata!.FileName })
            .FirstOrDefaultAsync(cancellationToken);

        if (clip is null)
        {
            return NotFound("Audio clip not found.");
        }

        Result<FileContent> download = await fileService.OpenFileContentAsync(clip.FileMetadataId, FileContentStreamingExtensions.ParseRequestedRange(Request), cancellationToken);

        if (!download.IsSuccess || download.Data is null)
        {
            return this.FileContentFailure(download.Error);
        }

        // From the stored file name, never the type the uploader's browser claimed.
        return await this.StreamFileContentAsync(download.Data, contentType: AudioClipFormats.ContentTypeFor(clip.FileName), cacheFor: TimeSpan.FromDays(1));
    }
}

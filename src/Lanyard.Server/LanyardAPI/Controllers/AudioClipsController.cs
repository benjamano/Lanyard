using Lanyard.API.Extensions;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Shared.AudioClips;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.API.Controllers;

/// <summary>
/// Serves audio clips to kiosks, which download them before playing them over the music.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AudioClipsController(IDbContextFactory<ApplicationDbContext> factory, IFileService fileService) : ControllerBase
{
    // A kiosk (its credential, or the media token in the URL it streams from) or a signed-in user,
    // as for songs (MusicController.GetAudioFile). Either way the tenant filter keeps it to their company.
    [HttpGet("{id:guid}/audio")]
    public async Task<IActionResult> GetAudio(Guid id, CancellationToken cancellationToken)
    {
        if (await KioskRequestAuthorization.AuthorizeAsync(HttpContext) == KioskRequestAccess.Denied)
        {
            return Challenge(KioskAuthenticationDefaults.Scheme);
        }

        await using ApplicationDbContext ctx = await factory.CreateDbContextAsync(cancellationToken);

        var clip = await ctx.AudioClips
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.Id == id && x.DeleteDate == null)
            .Select(x => new { x.FileMetadataId, FileName = x.FileMetadata!.FileName, x.Kind })
            .FirstOrDefaultAsync(cancellationToken);

        if (clip?.FileMetadataId is not Guid fileMetadataId)
        {
            return NotFound("Audio clip not found.");
        }

        Result<FileContent> download = await fileService.OpenFileContentAsync(fileMetadataId, FileContentStreamingExtensions.ParseRequestedRange(Request), cancellationToken);

        if (!download.IsSuccess || download.Data is null)
        {
            return this.FileContentFailure(download.Error);
        }

        // A typed announcement's audio changes under the same clip id when its words change, and
        // its words could name someone, so browsers don't keep it. (Kiosks cache clips themselves,
        // by ContentVersion, so they never relied on this header.)
        if (clip.Kind == AudioClipKind.Speech)
        {
            Response.Headers.CacheControl = "no-store";
        }

        // From the stored file name, never the type the uploader's browser claimed.
        return await this.StreamFileContentAsync(
            download.Data,
            contentType: AudioClipFormats.ContentTypeFor(clip.FileName),
            cacheFor: clip.Kind == AudioClipKind.Speech ? null : TimeSpan.FromDays(1));
    }
}

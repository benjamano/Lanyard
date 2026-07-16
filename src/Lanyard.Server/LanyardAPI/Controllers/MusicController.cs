using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MusicController(IDbContextFactory<ApplicationDbContext> factory, IFileService fileService, IClientSecretValidator clientSecretValidator) : ControllerBase
    {
        private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
        private readonly IFileService _fileService = fileService;
        private readonly IClientSecretValidator _clientSecretValidator = clientSecretValidator;

        [HttpGet("audio/{id}")]
        public async Task<IActionResult> GetAudioFile(Guid id, CancellationToken cancellationToken)
        {
            if (!ClientRequestAuthorization.IsAuthorized(HttpContext, _clientSecretValidator))
            {
                return Unauthorized();
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            Song? song = await ctx.Songs
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

            if (song is null)
            {
                return NotFound("Audio file not found.");
            }

            // Songs backed by an uploaded file resolve through the file service, which transparently
            // serves from local disk (dev) or the Railway bucket (prod).
            if (song.FileMetadataId is Guid fileMetadataId)
            {
                Result<Stream> download = await _fileService.DownloadFileAsync(fileMetadataId, cancellationToken);

                if (!download.Success || download.Data is null)
                {
                    return NotFound("Audio file not found.");
                }

                return File(download.Data, "audio/mpeg", enableRangeProcessing: true);
            }

            // Legacy / dev-scan songs store an absolute local path.
            if (!System.IO.File.Exists(song.FilePath))
            {
                return NotFound("Audio file not found.");
            }

            FileStream fileStream = System.IO.File.OpenRead(song.FilePath);

            return File(fileStream, "audio/mpeg", enableRangeProcessing: true);
        }
    }
}

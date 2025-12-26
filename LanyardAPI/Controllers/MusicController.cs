using Lanyard.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MusicController(IDbContextFactory<ApplicationDbContext> factory) : ControllerBase
    {
        IDbContextFactory<ApplicationDbContext> _factory = factory;

        [HttpGet("audio/{id}")]
        public async Task<IActionResult> GetAudioFile(Guid id)
        {
            using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string? filePath = await ctx.Songs
                .Where(s => s.Id == id)
                .Select(s => s.FilePath)
                .FirstOrDefaultAsync();

            if (!System.IO.File.Exists(filePath))
            {
                return NotFound("Audio file not found.");
            }

            FileStream fileStream = System.IO.File.OpenRead(filePath);

            return File(fileStream, "audio/mpeg", enableRangeProcessing: true);
        }
    }
}

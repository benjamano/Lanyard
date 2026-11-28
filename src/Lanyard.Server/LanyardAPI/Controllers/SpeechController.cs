using System.Security.Claims;
using Lanyard.Application.Services.AudioClips;
using Lanyard.Application.Services.Speech;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lanyard.API.Controllers;

/// <summary>
/// Plays back a spoken announcement in the browser before it's saved. Staff only: the preview is
/// made by the Blazor app and fetched by the same person's browser.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SpeechController(ISpeechPreviewStore previews) : ControllerBase
{
    // Another person's preview, an expired one and a made-up id are all just "not found".
    [HttpGet("preview/{id:guid}")]
    [Authorize(Roles = AudioClipRoles.Play)]
    public IActionResult GetPreview(Guid id)
    {
        string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null || previews.Get(id, userId) is not byte[] wav)
        {
            return NotFound();
        }

        // The words can name people, so the browser mustn't keep a copy.
        Response.Headers.CacheControl = "no-store";

        return File(wav, "audio/wav", enableRangeProcessing: true);
    }
}

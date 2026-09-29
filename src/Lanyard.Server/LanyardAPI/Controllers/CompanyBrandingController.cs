using Lanyard.Application.Services;
using Lanyard.Application.Services.Branding;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lanyard.API.Controllers
{
    [ApiController]
    [Route("api/companies")]
    public class CompanyBrandingController : ControllerBase
    {
        private readonly ICompanyLocationService _companyLocationService;
        private readonly IFileService _fileService;
        private readonly IAppIconService _appIconService;
        private readonly ILogger<CompanyBrandingController> _logger;

        public CompanyBrandingController(ICompanyLocationService companyLocationService, IFileService fileService,
            IAppIconService appIconService, ILogger<CompanyBrandingController> logger)
        {
            _companyLocationService = companyLocationService;
            _fileService = fileService;
            _appIconService = appIconService;
            _logger = logger;
        }

        // Raster image types only. An SVG served same-origin from this anonymous URL would
        // execute any embedded <script> when navigated to directly, and the admin-side
        // uploader's Accept="image/*" is only a client-side hint - this is the real gate.
        // Shared by both the logo and background-image endpoints below.
        private static readonly HashSet<string> AllowedImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "image/png",
            "image/jpeg",
            "image/gif",
            "image/webp"
        };

        // Deliberately its own anonymous endpoint, not an addition to FilesController's
        // gated /api/files/download/{id} route - it accepts only a companyId (never a raw
        // file id) and resolves LogoFileId server-side, so it can only ever serve whatever
        // an admin explicitly designated as that company's public logo.
        [HttpGet("{companyId:int}/logo")]
        [AllowAnonymous]
        [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
        public async Task<IActionResult> GetLogo(int companyId, CancellationToken cancellationToken)
        {
            Result<CompanyBrandingInfo> branding = await _companyLocationService.GetCompanyBrandingAsync(companyId);

            if (!branding.Success || branding.Data?.LogoFileId is not Guid logoFileId)
            {
                return NotFound();
            }

            Result<FileMetadata> meta = await _fileService.GetFileMetadataAsync(logoFileId, cancellationToken);
            string? contentType = meta.Data?.ContentType;

            // Resolved before opening the stream so a disallowed type never gets one opened.
            // NotFound (rather than an explanatory error) matches this endpoint's don't-leak-details posture.
            if (contentType is null || !AllowedImageContentTypes.Contains(contentType))
            {
                return NotFound();
            }

            Result<Stream> fileResult = await _fileService.DownloadFileAsync(logoFileId, cancellationToken);

            if (!fileResult.Success || fileResult.Data is null)
            {
                return NotFound();
            }

            return File(fileResult.Data, contentType);
        }

        // Same reasoning as GetLogo above: its own anonymous endpoint keyed by companyId,
        // never a raw file id, resolving BackgroundImageFileId server-side.
        [HttpGet("{companyId:int}/background")]
        [AllowAnonymous]
        [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
        public async Task<IActionResult> GetBackgroundImage(int companyId, CancellationToken cancellationToken)
        {
            Result<CompanyBrandingInfo> branding = await _companyLocationService.GetCompanyBrandingAsync(companyId);

            if (!branding.Success || branding.Data?.BackgroundImageFileId is not Guid backgroundImageFileId)
            {
                return NotFound();
            }

            Result<FileMetadata> meta = await _fileService.GetFileMetadataAsync(backgroundImageFileId, cancellationToken);
            string? contentType = meta.Data?.ContentType;

            if (contentType is null || !AllowedImageContentTypes.Contains(contentType))
            {
                return NotFound();
            }

            Result<Stream> fileResult = await _fileService.DownloadFileAsync(backgroundImageFileId, cancellationToken);

            if (!fileResult.Success || fileResult.Data is null)
            {
                return NotFound();
            }

            return File(fileResult.Data, contentType);
        }
    

        // The installed app's manifest for one company (linked from App.razor's <head>): the company's
        // name, colour and - if it has a logo - logo icons. A company that can't be found gets the
        // default λ manifest, so installing Lanyard never fails just because branding isn't set up.
        [HttpGet("{companyId:int}/manifest.webmanifest")]
        [AllowAnonymous]
        [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
        public async Task<IActionResult> GetManifest(int companyId)
        {
            Result<CompanyBrandingInfo> branding = await _companyLocationService.GetCompanyBrandingAsync(companyId);

            if (!branding.Success || branding.Data is null)
            {
                return Redirect("/manifest.webmanifest");
            }

            return Content(_appIconService.BuildCompanyManifestJson(branding.Data), "application/manifest+json");
        }

        // The company's logo as a square app icon. Like GetLogo it takes only a companyId and
        // resolves the logo server-side. Anything that stops the logo being usable falls back to the
        // matching static λ icon rather than a broken image on someone's home screen. The ?v= the
        // manifest adds changes with the logo, which is what lets this be cached for a day.
        [HttpGet("{companyId:int}/app-icon/{size:int}")]
        [AllowAnonymous]
        [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Any)]
        public async Task<IActionResult> GetAppIcon(int companyId, int size, [FromQuery] bool maskable, CancellationToken cancellationToken)
        {
            if (!_appIconService.SupportedSizes.Contains(size))
            {
                return NotFound();
            }

            string fallback = size switch
            {
                180 => "/apple-touch-icon.png",
                192 => "/icon-192.png",
                _ => maskable ? "/icon-maskable-512.png" : "/icon-512.png"
            };

            Result<CompanyBrandingInfo> branding = await _companyLocationService.GetCompanyBrandingAsync(companyId);

            if (!branding.Success || branding.Data?.LogoFileId is not Guid logoFileId)
            {
                return Redirect(fallback);
            }

            Result<byte[]> icon = await _appIconService.RenderLogoIconAsync(logoFileId, size,
                maskable ? AppIconPurpose.Maskable : AppIconPurpose.Any, cancellationToken);

            if (!icon.Success || icon.Data is null)
            {
                _logger.LogWarning("Falling back to the default app icon for {CompanyId}: {Error}", companyId, icon.Error);
                return Redirect(fallback);
            }

            return File(icon.Data, "image/png");
        }
    }
}

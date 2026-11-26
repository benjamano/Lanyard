using Lanyard.Application.Services;
using Lanyard.Application.Services.Branding;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Net.Http.Headers;

namespace Lanyard.API.Controllers
{
    [ApiController]
    [Route("api/companies")]
    public class CompanyBrandingController : ControllerBase
    {
        private readonly ICompanyLocationService _companyLocationService;
        private readonly IFileService _fileService;
        private readonly IAppIconService _appIconService;
        private readonly IMemoryCache _cache;
        private readonly ILogger<CompanyBrandingController> _logger;

        private static readonly TimeSpan VersionedMaxAge = TimeSpan.FromDays(365);
        private static readonly TimeSpan UnversionedMaxAge = TimeSpan.FromMinutes(5);

        public CompanyBrandingController(ICompanyLocationService companyLocationService, IFileService fileService,
            IAppIconService appIconService, IMemoryCache cache, ILogger<CompanyBrandingController> logger)
        {
            _companyLocationService = companyLocationService;
            _fileService = fileService;
            _appIconService = appIconService;
            _cache = cache;
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
        public async Task<IActionResult> GetLogo(int companyId, CancellationToken cancellationToken)
        {
            Result<CompanyBrandingInfo> branding = await _companyLocationService.GetCompanyBrandingAsync(companyId);

            if (!branding.Success || branding.Data?.LogoFileId is not Guid logoFileId)
            {
                return NotFound();
            }

            return await ServeBrandingImageAsync(logoFileId, cancellationToken);
        }

        // Same reasoning as GetLogo above: its own anonymous endpoint keyed by companyId,
        // never a raw file id, resolving BackgroundImageFileId server-side.
        [HttpGet("{companyId:int}/background")]
        [AllowAnonymous]
        public async Task<IActionResult> GetBackgroundImage(int companyId, CancellationToken cancellationToken)
        {
            Result<CompanyBrandingInfo> branding = await _companyLocationService.GetCompanyBrandingAsync(companyId);

            if (!branding.Success || branding.Data?.BackgroundImageFileId is not Guid backgroundImageFileId)
            {
                return NotFound();
            }

            return await ServeBrandingImageAsync(backgroundImageFileId, cancellationToken);
        }

        // A file's bytes never change under its id - a new logo is a new upload with a new id - so
        // the id is the ETag, and a URL whose ?v= is the current id can be cached by the browser for
        // good. Anything else (no ?v=, or an old one, e.g. in an email sent before the logo changed)
        // gets five minutes, and its revalidation is a 304 that never opens the file.
        private async Task<IActionResult> ServeBrandingImageAsync(Guid fileId, CancellationToken cancellationToken)
        {
            EntityTagHeaderValue etag = new($"\"{fileId:N}\"");
            bool versioned = string.Equals(Request.Query["v"], fileId.ToString("N"), StringComparison.OrdinalIgnoreCase);

            ResponseHeaders headers = Response.GetTypedHeaders();
            headers.ETag = etag;
            headers.CacheControl = versioned
                ? new CacheControlHeaderValue { Public = true, MaxAge = VersionedMaxAge, Extensions = { new NameValueHeaderValue("immutable") } }
                : new CacheControlHeaderValue { Public = true, MaxAge = UnversionedMaxAge };

            if (Request.GetTypedHeaders().IfNoneMatch.Any(x => x.Compare(etag, useStrongComparison: false)))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }

            string? contentType = await GetImageContentTypeAsync(fileId, cancellationToken);

            // Resolved before opening the stream so a disallowed type never gets one opened.
            // NotFound (rather than an explanatory error) matches this endpoint's don't-leak-details posture.
            if (contentType is null || !AllowedImageContentTypes.Contains(contentType))
            {
                Response.Headers.Remove(HeaderNames.ETag);
                Response.Headers.Remove(HeaderNames.CacheControl);
                return NotFound();
            }

            Result<Stream> fileResult = await _fileService.DownloadFileAsync(fileId, cancellationToken);

            if (!fileResult.Success || fileResult.Data is null)
            {
                Response.Headers.Remove(HeaderNames.ETag);
                Response.Headers.Remove(HeaderNames.CacheControl);
                return NotFound();
            }

            return File(fileResult.Data, contentType);
        }

        // Like the bytes, a file's content type never changes, so it's kept rather than re-read for
        // every logo request that misses the browser cache. Missing files aren't cached.
        private async Task<string?> GetImageContentTypeAsync(Guid fileId, CancellationToken cancellationToken)
        {
            string key = BrandingCache.ImageContentType(fileId);

            if (_cache.TryGetValue(key, out string? cached) && cached is not null)
            {
                return cached;
            }

            Result<FileMetadata> meta = await _fileService.GetFileMetadataAsync(fileId, cancellationToken);
            string? contentType = meta.Data?.ContentType;

            if (contentType is not null)
            {
                _cache.Set(key, contentType, BrandingCache.Ttl);
            }

            return contentType;
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

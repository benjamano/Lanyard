using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Kitchen;
using Lanyard.Application.Services.Legal;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Lanyard.API.Controllers
{
    /// <summary>
    /// The ordering API consumed by the public site (Lanyard.Reach.Web) on a customer's behalf.
    ///
    /// Two things about this controller are unlike the rest of the API and are deliberate:
    ///
    /// 1. It is never called by a browser. Reach proxies every request server-side, so no
    ///    customer ever holds a credential for this API, no CORS policy is needed, and the
    ///    server's Content-Security-Policy does not have to be relaxed. Callers authenticate
    ///    with the Reach shared secret, which is separate from the kiosk clients' secret.
    ///
    /// 2. It replaces the app-wide "ip-fixed" rate limit with an ordering-specific one. Under
    ///    the proxy model every customer in every venue reaches this controller from Reach's
    ///    single IP, so a per-IP limit of 25/min would throttle the entire customer base to
    ///    25 requests a minute between them - roughly two diners. See Program.cs.
    ///
    /// Every tenant-scoped endpoint takes companyId and the services re-check it against the
    /// data being asked for, so a bug in Reach's tenant resolution cannot leak one company's
    /// menu or orders onto another company's domain.
    /// </summary>
    [ApiController]
    [Route("api/ordering")]
    [AllowAnonymous]
    [EnableRateLimiting(OrderingRateLimits.ReadPolicy)]
    public class OrderingController : ControllerBase
    {
        private readonly IReachApiCredentialValidator _reachCredentialValidator;
        private readonly ITenantDirectoryService _tenantDirectory;
        private readonly IQrTableTokenService _tableTokens;
        private readonly IMenuService _menuService;
        private readonly IKitchenOrderService _orderService;
        private readonly IFileService _fileService;
        private readonly IOrderPaymentService _paymentService;

        private readonly ICompanyLegalDocumentService _legalDocuments;

        public OrderingController(
            IReachApiCredentialValidator reachCredentialValidator,
            ITenantDirectoryService tenantDirectory,
            IQrTableTokenService tableTokens,
            IMenuService menuService,
            IKitchenOrderService orderService,
            IFileService fileService,
            IOrderPaymentService paymentService,
            ICompanyLegalDocumentService legalDocuments)
        {
            _legalDocuments = legalDocuments;
            _reachCredentialValidator = reachCredentialValidator;
            _tenantDirectory = tenantDirectory;
            _tableTokens = tableTokens;
            _menuService = menuService;
            _orderService = orderService;
            _fileService = fileService;
            _paymentService = paymentService;
        }

        [HttpGet("tenants/by-host/{hostname}")]
        public async Task<IActionResult> GetTenantByHost(string hostname)
        {
            if (!_reachCredentialValidator.IsAuthorized(HttpContext))
            {
                return Unauthorized();
            }

            Result<TenantBrandingDto> result = await _tenantDirectory.GetTenantByHostnameAsync(hostname);

            return result.Success && result.Data is not null ? Ok(result.Data) : NotFound();
        }

        [HttpGet("tenants/by-slug/{slug}")]
        public async Task<IActionResult> GetTenantBySlug(string slug)
        {
            if (!_reachCredentialValidator.IsAuthorized(HttpContext))
            {
                return Unauthorized();
            }

            Result<TenantBrandingDto> result = await _tenantDirectory.GetTenantBySlugAsync(slug);

            return result.Success && result.Data is not null ? Ok(result.Data) : NotFound();
        }

        /// <summary>A company's legal identity, for rendering its ordering terms.</summary>
        [HttpGet("tenants/{companyId:int}/legal")]
        public async Task<IActionResult> GetTenantLegalDetails(int companyId)
        {
            if (!_reachCredentialValidator.IsAuthorized(HttpContext))
            {
                return Unauthorized();
            }

            Result<TenantLegalDetailsDto> result = await _tenantDirectory.GetLegalDetailsAsync(companyId);

            return result.Success && result.Data is not null ? Ok(result.Data) : NotFound();
        }

        /// <summary>
        /// One of a company's customer-facing legal documents, with its placeholders already
        /// replaced by that company's own details. The public site never sees the raw template
        /// and never does the substitution itself, so a document can only ever display the
        /// details of the company it belongs to.
        /// </summary>
        [HttpGet("tenants/{companyId:int}/documents/{documentType}")]
        public async Task<IActionResult> GetLegalDocument(int companyId, LegalDocumentType documentType)
        {
            if (!_reachCredentialValidator.IsAuthorized(HttpContext))
            {
                return Unauthorized();
            }

            Result<string> result = await _legalDocuments.GetPublishedAsync(companyId, documentType);

            return result.Success && result.Data is not null
                ? Ok(new LegalDocumentDto { Html = result.Data })
                : NotFound();
        }

        [HttpGet("tables/{token}")]
        public async Task<IActionResult> ResolveTable(string token, [FromQuery] int companyId)
        {
            if (!_reachCredentialValidator.IsAuthorized(HttpContext))
            {
                return Unauthorized();
            }

            Result<TableResolutionDto> result = await _tableTokens.ResolveAsync(token);

            if (!result.Success || result.Data is null)
            {
                return NotFound();
            }

            // A table code belongs to exactly one company. Scanning it while on a different
            // tenant's site is either a mistake or someone probing, and both get the same 404.
            if (result.Data.CompanyId != companyId)
            {
                return NotFound();
            }

            // The table is real; we just could not establish whether the kitchen is taking
            // orders. 503 rather than 404 so the customer is offered "try again" instead of
            // being told their QR code is wrong, or that a venue standing open is shut.
            if (result.Data.OrderingOpen is null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            return Ok(result.Data);
        }

        [HttpGet("locations/{locationId:int}/menu")]
        public async Task<IActionResult> GetMenu(int locationId, [FromQuery] int companyId)
        {
            if (!_reachCredentialValidator.IsAuthorized(HttpContext))
            {
                return Unauthorized();
            }

            Result<MenuDto> result = await _menuService.GetPublicMenuAsync(locationId, companyId);

            return result.Success && result.Data is not null ? Ok(result.Data) : NotFound();
        }

        /// <summary>
        /// Menu photo, keyed by item and never by file id - the same shape as
        /// CompanyBrandingController's logo endpoint, and for the same reason: it can only ever
        /// serve an image an admin explicitly attached to a menu item.
        /// </summary>
        [HttpGet("menu-items/{itemId:int}/image")]
        [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
        public async Task<IActionResult> GetMenuItemImage(int itemId, [FromQuery] int companyId, CancellationToken cancellationToken)
        {
            if (!_reachCredentialValidator.IsAuthorized(HttpContext))
            {
                return Unauthorized();
            }

            Result<Guid> fileIdResult = await _menuService.GetItemImageFileIdAsync(itemId, companyId);

            if (!fileIdResult.Success)
            {
                return NotFound();
            }

            Result<FileMetadata> meta = await _fileService.GetFileMetadataAsync(fileIdResult.Data, cancellationToken);
            string? contentType = meta.Data?.ContentType;

            // Resolved before the stream is opened, so a disallowed type never gets one opened.
            if (!PublicImageContentTypes.IsAllowed(contentType))
            {
                return NotFound();
            }

            Result<Stream> fileResult = await _fileService.DownloadFileAsync(fileIdResult.Data, cancellationToken);

            if (!fileResult.Success || fileResult.Data is null)
            {
                return NotFound();
            }

            return File(fileResult.Data, contentType!);
        }

        [HttpPost("orders")]
        [EnableRateLimiting(OrderingRateLimits.WritePolicy)]
        public async Task<IActionResult> CreateOrder([FromBody] CreateOrderRequestDto request, [FromQuery] int companyId)
        {
            if (!_reachCredentialValidator.IsAuthorized(HttpContext))
            {
                return Unauthorized();
            }

            Result<CreateOrderResultDto> result = await _orderService.CreateOrderAsync(request, companyId);

            // BadRequest rather than NotFound: these are messages written for the customer
            // ("we've just run out of chips"), and Reach shows them verbatim.
            return result.Success && result.Data is not null
                ? Ok(result.Data)
                : BadRequest(new { error = result.Error });
        }

        [HttpGet("orders/{orderToken:guid}/status")]
        public async Task<IActionResult> GetOrderStatus(Guid orderToken, [FromQuery] int companyId)
        {
            if (!_reachCredentialValidator.IsAuthorized(HttpContext))
            {
                return Unauthorized();
            }

            // Backstop for a slow or lost webhook, driven off the poll the customer is already
            // making. Cheap in the common case - it only calls Stripe for an order still
            // sitting at Pending.
            await _orderService.ReconcilePaymentAsync(orderToken, companyId);

            Result<OrderStatusDto> result = await _orderService.GetOrderStatusAsync(orderToken, companyId);

            return result.Success && result.Data is not null ? Ok(result.Data) : NotFound();
        }

        /// <summary>
        /// Stripe's payment webhook. This is what actually releases an order to the kitchen.
        /// </summary>
        /// <remarks>
        /// Called by Stripe directly, not through Reach, so it does not present the Reach
        /// credential - the signature on the payload is the authentication, and a payload that
        /// does not verify is rejected. It is also excluded from the ordering rate limits,
        /// which partition on a header Stripe does not send; webhook retries must not be
        /// throttled into dropping a payment confirmation.
        /// </remarks>
        [HttpPost("payments/webhook")]
        [AllowAnonymous]
        [EnableRateLimiting(OrderingRateLimits.WebhookPolicy)]
        public async Task<IActionResult> PaymentWebhook()
        {
            using StreamReader reader = new(HttpContext.Request.Body);
            string payload = await reader.ReadToEndAsync();

            Result<OrderPaymentWebhookResult> parsed = _paymentService.ParseWebhook(
                payload, Request.Headers["Stripe-Signature"].ToString());

            // Only a bad signature is rejected. Everything else - including events this app has
            // no interest in - is acknowledged, or Stripe retries it with backoff for days and
            // the endpoint starts looking broken in the dashboard.
            if (!parsed.Success || parsed.Data is null)
            {
                return BadRequest();
            }

            if (parsed.Data.Handled && parsed.Data.PaymentIntentId is string paymentIntentId)
            {
                if (parsed.Data.Succeeded)
                {
                    await _orderService.ConfirmPaymentAsync(paymentIntentId);
                }
                else if (parsed.Data.Failed)
                {
                    await _orderService.MarkPaymentFailedAsync(paymentIntentId);
                }
            }

            return Ok();
        }
    }
}

using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Kitchen;
using Lanyard.Application.Services.Locations;
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
    /// The kitchen queue and its figures, for anything that wants to render them outside the
    /// Blazor app - a purpose-built kitchen display, a wall panel, a second screen.
    ///
    /// This is the same data the /kitchen page and the kitchen dashboard widgets show, taken
    /// from the same services, so a custom client cannot end up disagreeing with the staff
    /// screens about what the kitchen is doing.
    ///
    /// Two ways in, and both are scoped to one venue.
    ///
    /// A device presents the kiosk shared secret, exactly as the other kiosk endpoints do. A
    /// person presents their staff cookie and must additionally hold Admin or CanManageKitchen
    /// and be a member of the venue they are asking about - the same bar the kitchen hub and the
    /// /kitchen page enforce.
    ///
    /// The membership check is the important half. This once accepted any authenticated cookie,
    /// which let a signed-in member of staff at one company read another company's live tickets,
    /// customer notes and takings, and change their order statuses, just by putting a different
    /// locationId in the URL.
    ///
    /// Deliberately not the Reach credential - that belongs to the anonymous customer site and
    /// has no business reading a venue's takings.
    /// </summary>
    [ApiController]
    [Route("api/kitchen")]
    [AllowAnonymous]
    [EnableRateLimiting("ip-fixed")]
    public class KitchenController : ControllerBase
    {
        private readonly IKitchenOrderService _orderService;
        private readonly IMenuService _menuService;
        private readonly IClientSecretValidator _clientSecretValidator;
        private readonly ICompanyLocationService _companyLocationService;

        public KitchenController(
            IKitchenOrderService orderService,
            IMenuService menuService,
            IClientSecretValidator clientSecretValidator,
            ICompanyLocationService companyLocationService)
        {
            _orderService = orderService;
            _menuService = menuService;
            _clientSecretValidator = clientSecretValidator;
            _companyLocationService = companyLocationService;
        }

        /// <summary>
        /// Whether this caller may act on this venue. Fails closed: anything it cannot establish
        /// is a refusal, never a default of "allowed".
        /// </summary>
        private async Task<bool> IsAuthorizedForLocationAsync(int locationId)
        {
            // A kiosk device authenticates as the installation, not as a person, and is already
            // trusted with the venue floor by the same secret. Read from the header or the query
            // exactly as ClientRequestAuthorization does, so the two cannot disagree.
            string? providedSecret = HttpContext.Request.Headers[ClientRequestAuthorization.SecretHeaderName].ToString();

            if (string.IsNullOrEmpty(providedSecret))
            {
                providedSecret = HttpContext.Request.Query[ClientRequestAuthorization.SecretQueryName].ToString();
            }

            if (!string.IsNullOrEmpty(providedSecret) && _clientSecretValidator.IsValid(providedSecret))
            {
                return true;
            }

            if (HttpContext.User?.Identity?.IsAuthenticated != true)
            {
                return false;
            }

            if (!HttpContext.User.IsInRole("Admin") && !HttpContext.User.IsInRole("CanManageKitchen"))
            {
                return false;
            }

            // An admin is not restricted to a venue; anyone else must belong to this one.
            if (HttpContext.User.IsInRole("Admin"))
            {
                return true;
            }

            string? userId = HttpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrWhiteSpace(userId))
            {
                return false;
            }

            Result<bool> membership = await _companyLocationService.IsUserMemberOfLocationAsync(userId, locationId);

            return membership.Success && membership.Data;
        }

        /// <summary>Open tickets for one venue, oldest first - the kitchen display's working set.</summary>
        [HttpGet("{locationId:int}/queue")]
        public async Task<IActionResult> GetQueue(int locationId)
        {
            if (!await IsAuthorizedForLocationAsync(locationId))
            {
                return Unauthorized();
            }

            Result<List<KitchenOrder>> result = await _orderService.GetOpenOrdersForLocationAsync(locationId);

            if (!result.Success || result.Data is null)
            {
                return BadRequest(new { error = result.Error });
            }

            return Ok(result.Data.Select(KitchenOrderService.ToTicket).ToList());
        }

        [HttpGet("{locationId:int}/stats")]
        public async Task<IActionResult> GetStats(int locationId, [FromQuery] KitchenStatsPeriod period = KitchenStatsPeriod.Today)
        {
            if (!await IsAuthorizedForLocationAsync(locationId))
            {
                return Unauthorized();
            }

            Result<KitchenStats> result = await _orderService.GetStatsAsync(locationId, period);

            if (!result.Success || result.Data is null)
            {
                return BadRequest(new { error = result.Error });
            }

            return Ok(new KitchenStatsDto
            {
                LocationId = locationId,
                Period = period,
                ServedCount = result.Data.ServedCount,
                CancelledCount = result.Data.CancelledCount,
                RefundedCount = result.Data.RefundedCount,
                TakingsCents = result.Data.TakingsCents,
                AverageSecondsToReady = result.Data.AverageSecondsToReady
            });
        }

        /// <summary>Menu items and their availability, so a client can 86 something from its own UI.</summary>
        [HttpGet("{locationId:int}/menu-items")]
        public async Task<IActionResult> GetMenuItems(int locationId)
        {
            if (!await IsAuthorizedForLocationAsync(locationId))
            {
                return Unauthorized();
            }

            Result<List<MenuItem>> result = await _menuService.GetItemsForLocationAsync(locationId, includeInactive: false);

            if (!result.Success || result.Data is null)
            {
                return BadRequest(new { error = result.Error });
            }

            return Ok(result.Data.Select(i => new MenuItemDto
            {
                Id = i.Id,
                Name = i.Name,
                Description = i.Description,
                PriceCents = i.PriceCents,
                IsAvailable = i.IsAvailable,
                HasImage = i.ImageFileId is not null,
                SortOrder = i.SortOrder
            }).ToList());
        }

        /// <summary>
        /// Advances a ticket. Cancelling is deliberately not reachable here: it decides what
        /// happens to the customer's money, and that belongs on a screen where a person is
        /// asked about the refund rather than in a fire-and-forget client call.
        /// </summary>
        [HttpPost("orders/{orderId:int}/status")]
        public async Task<IActionResult> SetStatus(int orderId, [FromBody] SetKitchenOrderStatusRequest request)
        {
            Result<int> orderLocation = await _orderService.GetLocationIdForOrderAsync(orderId);

            // Same "not found" whether the order does not exist or belongs to another venue, so
            // this cannot be walked to discover which order ids exist elsewhere.
            if (!orderLocation.Success || !await IsAuthorizedForLocationAsync(orderLocation.Data))
            {
                return Unauthorized();
            }

            if (request.Status == KitchenOrderStatus.Cancelled)
            {
                return BadRequest(new { error = "Cancel an order from the kitchen screen, so the refund can be decided." });
            }

            Result<KitchenOrder> result = await _orderService.SetOrderStatusAsync(orderId, request.Status);

            return result.Success && result.Data is not null
                ? Ok(KitchenOrderService.ToTicket(result.Data))
                : BadRequest(new { error = result.Error });
        }

        [HttpPost("menu-items/{itemId:int}/availability")]
        public async Task<IActionResult> SetAvailability(int itemId, [FromBody] SetMenuItemAvailabilityRequest request)
        {
            Result<int> itemLocation = await _menuService.GetLocationIdForItemAsync(itemId);

            if (!itemLocation.Success || !await IsAuthorizedForLocationAsync(itemLocation.Data))
            {
                return Unauthorized();
            }

            Result<bool> result = await _menuService.SetItemAvailabilityAsync(itemId, request.IsAvailable);

            return result.Success ? Ok() : BadRequest(new { error = result.Error });
        }
    }

    public class SetKitchenOrderStatusRequest
    {
        public KitchenOrderStatus Status { get; set; }
    }

    public class SetMenuItemAvailabilityRequest
    {
        public bool IsAvailable { get; set; }
    }
}

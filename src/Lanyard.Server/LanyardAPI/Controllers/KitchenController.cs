using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Kitchen;
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
    /// Authorised the same way the kiosk-facing endpoints already are
    /// (<see cref="ClientRequestAuthorization"/>): a signed-in staff cookie, or the kiosk shared
    /// secret for a device with no user to log in. Deliberately not the Reach credential - that
    /// belongs to the anonymous customer site and has no business reading a venue's takings.
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

        public KitchenController(
            IKitchenOrderService orderService,
            IMenuService menuService,
            IClientSecretValidator clientSecretValidator)
        {
            _orderService = orderService;
            _menuService = menuService;
            _clientSecretValidator = clientSecretValidator;
        }

        /// <summary>Open tickets for one venue, oldest first - the kitchen display's working set.</summary>
        [HttpGet("{locationId:int}/queue")]
        public async Task<IActionResult> GetQueue(int locationId)
        {
            if (!ClientRequestAuthorization.IsAuthorized(HttpContext, _clientSecretValidator))
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
            if (!ClientRequestAuthorization.IsAuthorized(HttpContext, _clientSecretValidator))
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
            if (!ClientRequestAuthorization.IsAuthorized(HttpContext, _clientSecretValidator))
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
            if (!ClientRequestAuthorization.IsAuthorized(HttpContext, _clientSecretValidator))
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
            if (!ClientRequestAuthorization.IsAuthorized(HttpContext, _clientSecretValidator))
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

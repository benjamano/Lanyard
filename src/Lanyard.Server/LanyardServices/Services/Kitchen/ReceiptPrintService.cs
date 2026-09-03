using Lanyard.Application.Services.Clients;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Kitchen;

public interface IReceiptPrintService
{
    /// <summary>
    /// Sends a paid order to the venue's kiosk to be printed. Does nothing, quietly, when the
    /// venue has no printer configured.
    /// </summary>
    Task PrintOrderAsync(int orderId);
}

/// <summary>
/// Prints kitchen receipts by handing them to the venue's own kiosk client.
///
/// The Lanyard server is hosted away from the venue and cannot reach a printer on the venue's
/// network, so the kiosk that is already connected over SignalR does the printing. This service
/// builds the ticket and sends it; everything about paper, drivers and printer names lives on the
/// kiosk.
///
/// Every failure here is swallowed after logging. This runs immediately after a payment has been
/// taken and an order committed, and a printer that is out of paper must not turn a successful
/// payment into an error the customer sees - the order is already on the kitchen screen, which
/// remains the source of truth. The ticket is a convenience on top of it.
/// </summary>
public class ReceiptPrintService(
    IDbContextFactory<ApplicationDbContext> factory,
    IClientService clientService,
    IHubContext<SignalRControlHub> hubContext,
    ILogger<ReceiptPrintService> logger) : IReceiptPrintService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IClientService _clientService = clientService;
    private readonly IHubContext<SignalRControlHub> _hubContext = hubContext;
    private readonly ILogger<ReceiptPrintService> _logger = logger;

    public async Task PrintOrderAsync(int orderId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            KitchenOrder? order = await ctx.KitchenOrders
                .AsNoTracking()
                .TagWithCallSite()
                .Include(o => o.Items)
                    .ThenInclude(i => i.Options)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order is null)
            {
                return;
            }

            var venue = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Where(l => l.Id == order.LocationId && l.IsActive)
                .Select(l => new { l.Name, l.TimeZoneId, l.FulfilmentMode, l.ReceiptPrinterClientId })
                .FirstOrDefaultAsync();

            // No printer configured is the normal case for a venue that works off the screen.
            if (venue?.ReceiptPrinterClientId is not Guid printerClientId)
            {
                return;
            }

            // Checked again at send time, not just when the printer was chosen. A kiosk that is
            // physically moved to another site keeps its id, so without this the old venue's
            // tickets would quietly follow the hardware to its new home.
            bool kioskIsAtThisVenue = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(c => c.Id == printerClientId && c.LocationId == order.LocationId);

            if (!kioskIsAtThisVenue)
            {
                _logger.LogWarning(
                    "Order {OrderId} was not printed: kiosk {ClientId} is no longer assigned to location {LocationId}",
                    orderId, printerClientId, order.LocationId);

                return;
            }

            // Asked of the live hub registry, not of the client row. MostRecentConnectionId
            // survives a disconnect and is served from a cache, so a kiosk that is switched off
            // still produces a plausible-looking connection id - and sending to a connection id
            // the hub does not know is a silent no-op, which would have this logging every lost
            // ticket as a success.
            Result<bool> isConnected = await _clientService.IsClientConnectedAsync(printerClientId);
            Result<string?> connection = await _clientService.GetClientCurrentConnectionIdAsync(printerClientId);

            if (!isConnected.IsSuccess || !isConnected.Data
                || !connection.IsSuccess || string.IsNullOrWhiteSpace(connection.Data))
            {
                // Warning rather than error: a kiosk that is off or restarting is an ordinary
                // state, but staff need to know tickets are not coming out.
                _logger.LogWarning(
                    "Order {OrderId} was not printed: the receipt printer client for location {LocationId} is not connected",
                    orderId, order.LocationId);

                return;
            }

            // Claims the print before sending it. A conditional update is atomic in the database,
            // so of two callers reconciling the same payment at the same moment exactly one sees
            // a row affected and exactly one ticket reaches the pass.
            int claimed = await ctx.KitchenOrders
                .Where(o => o.Id == orderId && o.ReceiptPrintedDate == null)
                .ExecuteUpdateAsync(o => o.SetProperty(x => x.ReceiptPrintedDate, DateTime.UtcNow));

            if (claimed == 0)
            {
                _logger.LogInformation(
                    "Order {OrderId} already has a printed ticket; not printing a second one", orderId);

                return;
            }

            KitchenReceiptDto receipt = BuildReceipt(order, venue.Name, venue.TimeZoneId, venue.FulfilmentMode);

            await _hubContext.Clients.Client(connection.Data).SendAsync(KitchenPrinting.PrintCommand, receipt);

            _logger.LogInformation("Sent order {OrderId} to the receipt printer for location {LocationId}",
                orderId, order.LocationId);
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown - see the class summary. The money has been taken and the
            // order is on the kitchen screen; failing here must not undo either.
            _logger.LogError(ex, "Failed to send order {OrderId} to the receipt printer", orderId);
        }
    }

    private static KitchenReceiptDto BuildReceipt(
        KitchenOrder order, string venueName, string? timeZoneId, OrderFulfilmentMode mode)
    {
        return new KitchenReceiptDto
        {
            OrderId = order.Id,
            LocationId = order.LocationId,
            VenueName = venueName,
            TableLabel = order.TableLabelSnapshot,
            PlacedAt = FormatLocal(order.CreateDate, timeZoneId),
            IsTableService = mode == OrderFulfilmentMode.TableService,
            CustomerNote = order.CustomerNote,
            TotalCents = order.TotalCents,
            Lines = [.. order.Items.Select(i => new KitchenReceiptLineDto
            {
                Quantity = i.Quantity,
                Name = i.MenuItemNameSnapshot,
                Options = [.. i.Options.Select(o => o.OptionNameSnapshot)],

                // Named here rather than on the kiosk, so the printed ticket and the kitchen
                // screen can never disagree about what a dish contains.
                Allergens = [.. i.ContainsAllergensSnapshot.Split().Select(a => a.DisplayName())]
            })]
        };
    }

    /// <summary>
    /// The kitchen reads this off paper, so it is formatted in the venue's own time before it
    /// leaves the server. A ticket that says 17:04 when the clock on the wall says 18:04 is worse
    /// than useless when someone is working out which order is late.
    /// </summary>
    private static string FormatLocal(DateTime utc, string? timeZoneId)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(timeZoneId))
            {
                TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

                return TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString("HH:mm");
            }
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Fall through to UTC rather than lose the ticket over a mistyped zone.
        }

        return utc.ToString("HH:mm");
    }
}

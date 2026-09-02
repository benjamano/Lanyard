using Lanyard.Client.Printing;
using Lanyard.Shared.DTO;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.Controllers;

/// <summary>
/// Receives kitchen receipts from the server and prints them.
///
/// The server cannot reach a printer on the venue's network, so this kiosk - already connected
/// over the same hub that carries DMX and projection - does the printing. The server sends a
/// finished ticket; nothing here queries anything.
/// </summary>
public class ReceiptPrinterController(
    IReceiptPrinterService receiptPrinterService,
    ILogger<ReceiptPrinterController> logger)
{
    private readonly IReceiptPrinterService _receiptPrinterService = receiptPrinterService;
    private readonly ILogger<ReceiptPrinterController> _logger = logger;

    public void Register(HubConnection connection)
    {
        connection.On<KitchenReceiptDto>(KitchenPrinting.PrintCommand, receipt =>
        {
            _logger.LogInformation("Received order {OrderId} to print for {TableLabel}",
                receipt.OrderId, receipt.TableLabel);

            // Detached from the hub's message pump. Print() blocks until the Windows spooler
            // accepts the job, and SignalR runs a connection's handlers one at a time, so a
            // printer that is asleep, jammed or out of paper would otherwise hold up every DMX,
            // music and projection command to this kiosk until it recovered. Same reasoning, and
            // same shape, as ProjectionProgramController.
            _ = Task.Run(() => _receiptPrinterService.Print(receipt));
        });
    }
}

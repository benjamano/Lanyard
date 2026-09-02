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

    /// <summary>
    /// Must match ReceiptPrintService.PrintCommand on the server exactly. A mismatch drops the
    /// event silently, with no error at either end.
    /// </summary>
    public const string PrintCommand = "PrintKitchenReceipt";

    public void Register(HubConnection connection)
    {
        connection.On<KitchenReceiptDto>(PrintCommand, receipt =>
        {
            _logger.LogInformation("Received order {OrderId} to print for {TableLabel}",
                receipt.OrderId, receipt.TableLabel);

            _receiptPrinterService.Print(receipt);
        });
    }
}

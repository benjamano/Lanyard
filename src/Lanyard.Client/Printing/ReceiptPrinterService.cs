using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Linq;
using Lanyard.Shared.DTO;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.Printing;

public interface IReceiptPrinterService
{
    void Print(KitchenReceiptDto receipt);
}

/// <summary>
/// Prints a kitchen receipt on whatever printer this machine has.
///
/// Deliberately goes through the Windows print driver rather than writing raw ESC/POS. That works
/// with a thermal roll printer, a laser printer, or a PDF writer, which matters because the venue
/// has not chosen its hardware yet - and swapping to ESC/POS later is a change in here alone.
///
/// The layout is plain monospaced text sized to the paper the driver reports, so an 80mm roll and
/// an A4 sheet both come out readable without the caller knowing which it is.
/// </summary>
public class ReceiptPrinterService(ILogger<ReceiptPrinterService> logger) : IReceiptPrinterService
{
    private readonly ILogger<ReceiptPrinterService> _logger = logger;

    /// <summary>
    /// Overrides the system default printer. Set LANYARD_RECEIPT_PRINTER to the printer's exact
    /// Windows name when the kitchen printer is not the default on this machine.
    /// </summary>
    private const string PrinterNameVariable = "LANYARD_RECEIPT_PRINTER";

    /// <summary>Characters per line on an 80mm roll at this font size.</summary>
    private const int ReceiptWidth = 32;

    private static readonly string Separator = new('-', ReceiptWidth);

    public void Print(KitchenReceiptDto receipt)
    {
        try
        {
            List<string> lines = BuildLines(receipt);
            int lineIndex = 0;

            using PrintDocument document = new();

            string? configuredPrinter = Environment.GetEnvironmentVariable(PrinterNameVariable);

            if (!string.IsNullOrWhiteSpace(configuredPrinter))
            {
                document.PrinterSettings.PrinterName = configuredPrinter;
            }

            // Checked rather than assumed: setting an unknown PrinterName does not throw, it just
            // silently leaves IsValid false and prints nothing at all.
            if (!document.PrinterSettings.IsValid)
            {
                _logger.LogError(
                    "Cannot print order {OrderId}: printer '{PrinterName}' is not available on this machine",
                    receipt.OrderId, document.PrinterSettings.PrinterName);

                return;
            }

            document.DocumentName = $"Order {receipt.OrderId} - {receipt.TableLabel}";

            // The default print controller shows a WinForms progress dialog. On a fullscreen kiosk
            // that appears over the customer-facing display and takes focus off WebView2.
            document.PrintController = new StandardPrintController();

            // Roll printers report a short page, and the default one-inch margins can swallow all
            // of it. Small fixed margins leave a printable band on any paper this might meet.
            document.DefaultPageSettings.Margins = new Margins(20, 20, 20, 20);

            // GenericMonospace rather than a named face: columns line up, and it exists on every
            // Windows install without depending on which fonts the venue happens to have.
            using Font font = new(FontFamily.GenericMonospace, 9f);

            document.PrintPage += (_, e) =>
            {
                if (e.Graphics is null)
                {
                    return;
                }

                int startedAt = lineIndex;
                float y = e.MarginBounds.Top;
                float lineHeight = font.GetHeight(e.Graphics);

                while (lineIndex < lines.Count && y + lineHeight <= e.MarginBounds.Bottom)
                {
                    e.Graphics.DrawString(lines[lineIndex], font, Brushes.Black, e.MarginBounds.Left, y);
                    y += lineHeight;
                    lineIndex++;
                }

                // A page with no room for even one line would otherwise leave lineIndex untouched
                // and ask for another page forever: blank sheets until the roll ran out, with
                // Print() never returning and this kiosk's hub connection stuck behind it.
                if (lineIndex == startedAt)
                {
                    _logger.LogError(
                        "Cannot print order {OrderId}: printer '{PrinterName}' reports a printable height smaller than one line",
                        receipt.OrderId, document.PrinterSettings.PrinterName);

                    e.HasMorePages = false;

                    return;
                }

                // A long order runs onto a second sheet rather than being silently truncated.
                e.HasMorePages = lineIndex < lines.Count;
            };

            document.Print();

            _logger.LogInformation("Printed order {OrderId} for {TableLabel}", receipt.OrderId, receipt.TableLabel);
        }
        catch (Exception ex)
        {
            // Never rethrown. This runs on a SignalR callback; letting it escape would tear down
            // the hub connection and take music, DMX and projection with it because a printer
            // jammed.
            _logger.LogError(ex, "Failed to print order {OrderId}", receipt.OrderId);
        }
    }

    /// <summary>
    /// The ticket, as lines of text. Ordered the way a kitchen reads one: where it is going first,
    /// then what to make, with the allergens attached to the line they belong to rather than
    /// gathered into a footnote nobody looks at.
    /// </summary>
    private static List<string> BuildLines(KitchenReceiptDto receipt)
    {
        List<string> lines =
        [
            receipt.VenueName,
            Separator,
            receipt.IsTableService ? $"DELIVER TO: {receipt.TableLabel}" : $"COLLECTION: {receipt.TableLabel}",
            $"Order #{receipt.OrderId}    {receipt.PlacedAt}",
            Separator,
            string.Empty
        ];

        foreach (KitchenReceiptLineDto line in receipt.Lines)
        {
            lines.Add($"{line.Quantity} x {line.Name}");

            if (line.Options.Count > 0)
            {
                lines.Add($"    {string.Join(", ", line.Options)}");
            }

            if (line.Allergens.Count > 0)
            {
                lines.Add($"    ALLERGENS: {string.Join(", ", line.Allergens)}");
            }

            lines.Add(string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(receipt.CustomerNote))
        {
            lines.Add(Separator);
            lines.Add("NOTE FROM CUSTOMER:");
            lines.Add(receipt.CustomerNote!);
            lines.Add(string.Empty);
        }

        lines.Add(Separator);
        lines.Add($"Total  £{receipt.TotalCents / 100m:0.00}");

        // Trailing blank lines so a roll printer's cutter does not slice through the total.
        lines.AddRange(Enumerable.Repeat(string.Empty, 4));

        return lines;
    }
}

using Lanyard.Infrastructure.DTO.Parties;
using Lanyard.Infrastructure.Enum;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Lanyard.Application.Services.Parties;

// The voucher and feedback form. Each is either the company's own artwork with the party's
// details printed into its boxes, or - with no artwork uploaded - Lanyard's standard design.
//
// Both are usually A5. They print at actual size on A4: two A5 portrait pieces side by side on a
// landscape sheet (two A5 landscape ones stacked on a portrait sheet), with a cut line between,
// the way the venue's own voucher sheet is laid out. Printing everything puts the voucher and the
// feedback form on the same sheet.
public static partial class PartyDocumentRenderer
{
    public const string HandwrittenFont = "Chewy";

    private const float A4Short = 210f;
    private const float A4Long = 297f;

    // The standard designs are A5 portrait.
    private const float StandardWidthMm = 148f;
    private const float StandardHeightMm = 210f;

    private const int DefaultVoucherMonths = 12;

    private static void RegisterFonts()
    {
        using Stream? font = typeof(PartyDocumentRenderer).Assembly.GetManifestResourceStream("Lanyard.Fonts.Chewy-Regular.ttf");

        if (font is not null)
        {
            FontManager.RegisterFontWithCustomName(HandwrittenFont, font);
        }
    }

    private enum SheetSlot
    {
        // Fits half of a landscape A4: goes side by side.
        PortraitHalf,

        // Fits half of a portrait A4: goes one above the other.
        LandscapeHalf,

        // Needs a sheet to itself.
        Full
    }

    private static void ComposeArtworkSheets(IDocumentContainer container, PartyDocumentModel model, PartyArtworkDocument[] documents)
    {
        List<Placed> pieces = documents
            .Select(document =>
            {
                (float width, float height) = PieceSize(model, document);
                return new Placed(document, width, height, SlotFor(width, height));
            })
            .ToList();

        for (int i = 0; i < pieces.Count; i++)
        {
            Placed piece = pieces[i];

            // Pair this piece with the next one when both fit the same half-sheet.
            Placed? partner = piece.Slot != SheetSlot.Full && i + 1 < pieces.Count && pieces[i + 1].Slot == piece.Slot
                ? pieces[++i]
                : null;

            container.Page(page =>
            {
                page.Margin(0);

                switch (piece.Slot)
                {
                    case SheetSlot.PortraitHalf:
                        page.Size(PageSizes.A4.Landscape());
                        page.Content().Row(row =>
                        {
                            row.ConstantItem(HalfLong, Unit.Millimetre).Element(x => Slot(x, model, piece, HalfLong, A4Short - Trim));
                            row.ConstantItem(CutLine, Unit.Millimetre).Element(CutLineVertical);
                            row.ConstantItem(HalfLong, Unit.Millimetre).Element(x => Slot(x, model, partner, HalfLong, A4Short - Trim));
                        });
                        break;

                    case SheetSlot.LandscapeHalf:
                        page.Size(PageSizes.A4);
                        page.Content().Column(column =>
                        {
                            column.Item().Height(HalfLong, Unit.Millimetre).Element(x => Slot(x, model, piece, A4Short - Trim, HalfLong));
                            column.Item().Height(CutLine, Unit.Millimetre).Element(CutLineHorizontal);
                            column.Item().Height(HalfLong, Unit.Millimetre).Element(x => Slot(x, model, partner, A4Short - Trim, HalfLong));
                        });
                        break;

                    default:
                        bool landscape = piece.Width > piece.Height;
                        page.Size(landscape ? PageSizes.A4.Landscape() : PageSizes.A4);
                        page.Content().Element(x => Slot(x, model, piece, (landscape ? A4Long : A4Short) - Trim, (landscape ? A4Short : A4Long) - Trim));
                        break;
                }
            });
        }
    }

    private record Placed(PartyArtworkDocument Document, float Width, float Height, SheetSlot Slot);

    // Half an A4's long side, less the cut line between the halves.
    private const float CutLine = 0.4f;
    private const float HalfLong = (A4Long - CutLine) / 2;

    // Kept clear of the page edge so millimetre/point rounding can't make a piece a hair too big
    // for its page, which QuestPDF treats as a layout error rather than a tiny overlap.
    private const float Trim = 0.5f;

    // Centres a piece in its space at its real size, or scaled down to fit when it's bigger (A4
    // artwork on an A4 page, say). Scaled by hand rather than with ScaleToFit, which can't settle
    // two pieces side by side in one row.
    private static void Slot(IContainer container, PartyDocumentModel model, Placed? piece, float availableWidth, float availableHeight)
    {
        if (piece is null)
        {
            return;
        }

        float scale = Math.Min(1f, Math.Min(availableWidth / piece.Width, availableHeight / piece.Height)) * 0.998f;

        container.AlignCenter().AlignMiddle()
            .Width(piece.Width * scale, Unit.Millimetre)
            .Height(piece.Height * scale, Unit.Millimetre)
            .Scale(scale)
            .Element(x => Piece(x, model, piece.Document));
    }

    private static (float Width, float Height) PieceSize(PartyDocumentModel model, PartyArtworkDocument document)
    {
        PartyArtwork? artwork = ArtworkFor(model, document);

        return artwork?.Artwork is null
            ? (StandardWidthMm, StandardHeightMm)
            : ((float)artwork.Template.WidthMm, (float)artwork.Template.HeightMm);
    }

    private static SheetSlot SlotFor(float width, float height)
    {
        const float slack = 0.5f;

        if (width <= A4Long / 2 + slack && height <= A4Short + slack)
        {
            return SheetSlot.PortraitHalf;
        }

        if (width <= A4Short + slack && height <= A4Long / 2 + slack)
        {
            return SheetSlot.LandscapeHalf;
        }

        return SheetSlot.Full;
    }

    private static PartyArtwork? ArtworkFor(PartyDocumentModel model, PartyArtworkDocument document) =>
        document == PartyArtworkDocument.Voucher ? model.Voucher : model.Feedback;

    private static void Piece(IContainer container, PartyDocumentModel model, PartyArtworkDocument document)
    {
        PartyArtwork? artwork = ArtworkFor(model, document);

        if (artwork?.Artwork is not byte[] image)
        {
            IContainer standard = container.Width(StandardWidthMm, Unit.Millimetre).Height(StandardHeightMm, Unit.Millimetre);

            if (document == PartyArtworkDocument.Voucher)
            {
                StandardVoucher(standard, model);
            }
            else
            {
                StandardFeedback(standard, model);
            }

            return;
        }

        string? font = artwork.Template.Font == PartyTemplateFont.Handwritten ? HandwrittenFont : null;

        container
            .Width((float)artwork.Template.WidthMm, Unit.Millimetre)
            .Height((float)artwork.Template.HeightMm, Unit.Millimetre)
            .Layers(layers =>
            {
                layers.PrimaryLayer().Image(image).FitArea();

                foreach (PartyTemplateField field in artwork.Fields)
                {
                    string text = FieldValue(field.Kind, model);

                    if (text.Length > 0)
                    {
                        layers.Layer().Element(x => FieldBox(x, field, text, font));
                    }
                }
            });
    }

    private static void FieldBox(IContainer container, PartyTemplateField field, string text, string? font)
    {
        IContainer box = container
            .TranslateX((float)field.XMm, Unit.Millimetre)
            .TranslateY((float)field.YMm, Unit.Millimetre)
            .Width((float)field.WidthMm, Unit.Millimetre)
            .Height((float)field.HeightMm, Unit.Millimetre);

        box = field.Rotation switch
        {
            90 => box.RotateRight(),
            270 => box.RotateLeft(),
            _ => box
        };

        box = field.Align == PartyTextAlign.Centre ? box.AlignCenter() : box.AlignLeft();

        // Shrinks a long name to fit its box rather than spilling over the artwork.
        TextBlockDescriptor descriptor = box.AlignMiddle().ScaleToFit().Text(text);
        descriptor.FontSize((float)field.FontSize).FontColor(field.ColourHex);

        if (font is not null)
        {
            descriptor.FontFamily(font);
        }
    }

    public static string FieldValue(PartyTemplateFieldKind kind, PartyDocumentModel model)
    {
        DateOnly partyDate = RotaTime.LocalDate(model.Booking.StartUtc);

        return kind switch
        {
            PartyTemplateFieldKind.PresentedTo => model.Booking.ChildName,
            PartyTemplateFieldKind.PartyDate => partyDate.ToString("d MMMM yyyy", Uk),
            PartyTemplateFieldKind.ExpiryDate => VoucherExpiry(model).ToString("d MMMM yyyy", Uk),
            PartyTemplateFieldKind.AuthorisedBy => model.AuthorisedBy ?? string.Empty,
            PartyTemplateFieldKind.PartyName => $"{model.Booking.ChildName}'s party",
            PartyTemplateFieldKind.PartyHost => model.HostName ?? string.Empty,
            _ => model.Booking.ChildAgeTurning?.ToString(Uk) ?? string.Empty
        };
    }

    public static DateOnly VoucherExpiry(PartyDocumentModel model) =>
        RotaTime.LocalDate(model.Booking.StartUtc).AddMonths(model.Voucher?.Template.VoucherValidityMonths ?? DefaultVoucherMonths);

    private static void CutLineVertical(IContainer container) =>
        container.AlignCenter().LineVertical(0.6f).LineColor(Colors.Grey.Lighten1).LineDashPattern([4, 4]);

    private static void CutLineHorizontal(IContainer container) =>
        container.AlignMiddle().LineHorizontal(0.6f).LineColor(Colors.Grey.Lighten1).LineDashPattern([4, 4]);

    // ---- Standard designs, for companies that haven't uploaded their own artwork ----

    private static void StandardVoucher(IContainer container, PartyDocumentModel model)
    {
        container.Padding(8, Unit.Millimetre).Border(2.5f).BorderColor(model.AccentColorHex).Padding(7, Unit.Millimetre).Column(column =>
        {
            column.Spacing(10);

            if (model.LogoBytes is not null)
            {
                column.Item().AlignCenter().Height(18, Unit.Millimetre).Image(model.LogoBytes).FitArea();
            }

            column.Item().PaddingTop(6).AlignCenter().Text("Happy Birthday!")
                .FontFamily(HandwrittenFont).FontSize(40).FontColor(model.AccentColorHex);
            column.Item().AlignCenter().Text("Free Return Voucher")
                .FontFamily(HandwrittenFont).FontSize(24).FontColor(Colors.Grey.Darken3);

            column.Item().PaddingTop(14).Column(rows =>
            {
                rows.Spacing(9);
                VoucherRow(rows.Item(), "Presented to", FieldValue(PartyTemplateFieldKind.PresentedTo, model));
                VoucherRow(rows.Item(), "Party date", FieldValue(PartyTemplateFieldKind.PartyDate, model));
                VoucherRow(rows.Item(), "Expiry date", FieldValue(PartyTemplateFieldKind.ExpiryDate, model));
                VoucherRow(rows.Item(), "Authorised by", FieldValue(PartyTemplateFieldKind.AuthorisedBy, model));
            });

            column.Item().ExtendVertical().AlignBottom().AlignCenter()
                .Text(model.CompanyName is { Length: > 0 } company ? $"Valid for one free return visit to {company}." : "Valid for one free return visit.")
                .FontSize(9).FontColor(Colors.Grey.Darken1);
        });
    }

    private static void VoucherRow(IContainer container, string label, string value)
    {
        container.Row(row =>
        {
            row.ConstantItem(34, Unit.Millimetre).AlignMiddle().Text($"{label}:").FontSize(11).SemiBold();
            row.RelativeItem().Height(10, Unit.Millimetre).Background(Colors.Grey.Lighten4).Border(0.75f).BorderColor(Colors.Grey.Lighten1)
                .PaddingHorizontal(6).AlignMiddle().ScaleToFit().Text(value).FontFamily(HandwrittenFont).FontSize(16);
        });
    }

    private static void StandardFeedback(IContainer container, PartyDocumentModel model)
    {
        string[] ratingQuestions =
        [
            "How was your greeting when you arrived?",
            "How well was your party explained to you? (where your table is, eating time etc.)",
            "How would you rate your party host?",
            "How would you rate your overall experience with us?"
        ];

        container.Padding(9, Unit.Millimetre).Column(column =>
        {
            column.Spacing(8);

            column.Item().Row(row =>
            {
                row.RelativeItem().AlignMiddle().Text("Party Feedback").FontFamily(HandwrittenFont).FontSize(28).FontColor(model.AccentColorHex);

                if (model.LogoBytes is not null)
                {
                    row.ConstantItem(32, Unit.Millimetre).Height(14, Unit.Millimetre).AlignRight().Image(model.LogoBytes).FitArea();
                }
            });

            column.Item().Text(text =>
            {
                text.Span("Party: ").SemiBold();
                text.Span(FieldValue(PartyTemplateFieldKind.PartyName, model)).FontFamily(HandwrittenFont).FontSize(13);
            });

            column.Item().Text(text =>
            {
                text.Span("Your party host: ").SemiBold();
                text.Span(FieldValue(PartyTemplateFieldKind.PartyHost, model)).FontFamily(HandwrittenFont).FontSize(13);
            });

            foreach (string question in ratingQuestions)
            {
                column.Item().PaddingTop(4).Column(block =>
                {
                    block.Spacing(4);
                    block.Item().Text(question).FontSize(10).SemiBold();
                    block.Item().Row(row =>
                    {
                        foreach (string option in new[] { "Poor", "Okay", "Good", "Excellent" })
                        {
                            row.RelativeItem().Row(choice =>
                            {
                                choice.ConstantItem(4, Unit.Millimetre).Height(4, Unit.Millimetre).Border(0.9f);
                                choice.ConstantItem(2, Unit.Millimetre);
                                choice.RelativeItem().Text(option).FontSize(9);
                            });
                        }
                    });
                });
            }

            column.Item().PaddingTop(4).Row(row =>
            {
                row.AutoItem().AlignMiddle().Text("Would you have your party with us again?").FontSize(10).SemiBold();
                row.ConstantItem(6, Unit.Millimetre);

                foreach (string option in new[] { "Yes", "No" })
                {
                    row.ConstantItem(4, Unit.Millimetre).Height(4, Unit.Millimetre).Border(0.9f);
                    row.ConstantItem(2, Unit.Millimetre);
                    row.ConstantItem(10, Unit.Millimetre).AlignMiddle().Text(option).FontSize(9);
                }
            });

            column.Item().PaddingTop(4).Text("Additional comments or suggestions:").FontSize(10).SemiBold();
            column.Item().Height(42, Unit.Millimetre).Border(0.75f).BorderColor(Colors.Grey.Medium);

            column.Item().AlignCenter()
                .Text(model.CompanyName is { Length: > 0 } company
                    ? $"Thank you for choosing {company} to celebrate your special occasion!"
                    : "Thank you for celebrating your special occasion with us!")
                .FontSize(9).FontColor(Colors.Grey.Darken1);
        });
    }
}

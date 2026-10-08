using System.Globalization;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Lanyard.Application.Services.Parties;

// Everything a party's printouts need, already loaded and checked.
public record PartyDocumentModel(
    PartyBooking Booking,
    string? HostName,
    IReadOnlyList<PartyMenuItem> Menu,
    byte[]? LogoBytes,
    string AccentColorHex);

// Lays out the party printouts. Pure (data in, PDF bytes out) so the layouts can be changed or
// replaced without touching how the data is loaded, and tested without a database.
//
// The layouts follow the venue's own paper sheets: the "RESERVED FOR ... PARTY" table card, the
// hot food tally sheet (each meal with its allergens and a row per side to count on) and the cold
// sheet (a row per child for their sandwich choice, with an allergy panel). Food is chosen when
// the children arrive, so the sheets print with the party details filled in and the rest blank.
public static class PartyDocumentRenderer
{
    private static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("en-GB");

    // Rows left over on the cold sheet for children who turn up unannounced.
    private const int SpareRows = 2;

    private const float CellPadding = 5;

    // See CertificateService: QuestPDF refuses to render until a licence is chosen.
    static PartyDocumentRenderer()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static byte[] Render(PartyDocumentModel model, PartyDocumentType type) =>
        Document.Create(container =>
        {
            switch (type)
            {
                case PartyDocumentType.ReservedCard:
                    ComposeReservedCard(container, model);
                    break;
                case PartyDocumentType.FoodSheet:
                    ComposeFoodSheet(container, model);
                    break;
                default:
                    ComposeReservedCard(container, model);
                    ComposeFoodSheet(container, model);
                    break;
            }
        })
        // The title is what the browser tab shows when the PDF opens.
        .WithMetadata(new DocumentMetadata { Title = Title(model, type), Author = "Lanyard" })
        .GeneratePdf();

    private static string Title(PartyDocumentModel model, PartyDocumentType type)
    {
        string what = type switch
        {
            PartyDocumentType.ReservedCard => "Reserved table card",
            PartyDocumentType.FoodSheet => model.Booking.MenuType == PartyMenuType.Cold ? "Cold food sheet" : "Hot food sheet",
            _ => "Party documents"
        };

        return $"{what} - {model.Booking.ChildName}'s party";
    }

    private static void ComposeFoodSheet(IDocumentContainer container, PartyDocumentModel model)
    {
        if (model.Booking.MenuType == PartyMenuType.Cold)
        {
            ComposeColdSheet(container, model);
        }
        else
        {
            ComposeHotSheet(container, model);
        }
    }

    // A4 landscape, folded along the middle into a tent: the top half is printed upside down so
    // the card reads the right way up from both sides of the table.
    private static void ComposeReservedCard(IDocumentContainer container, PartyDocumentModel model)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(0);

            page.Content().Column(column =>
            {
                float half = PageSizes.A4.Landscape().Height / 2;

                column.Item().Height(half).RotateRight().RotateRight().Element(x => ReservedCardFace(x, model));
                column.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                column.Item().Height(half - 0.5f).Element(x => ReservedCardFace(x, model));
            });
        });
    }

    private static void ReservedCardFace(IContainer container, PartyDocumentModel model)
    {
        container.PaddingVertical(24).PaddingHorizontal(48).AlignMiddle().Column(column =>
        {
            column.Spacing(6);

            if (model.LogoBytes is not null)
            {
                column.Item().AlignCenter().Height(40).Image(model.LogoBytes).FitHeight();
            }

            column.Item().AlignCenter().Text("RESERVED FOR").FontSize(26).SemiBold().FontColor(Colors.Grey.Darken3);
            column.Item().Height(90).AlignCenter().AlignMiddle().ScaleToFit()
                .Text(model.Booking.ChildName.ToUpper(Uk)).FontSize(80).Bold().FontColor(model.AccentColorHex);
            column.Item().AlignCenter().Text("PARTY").FontSize(26).SemiBold().FontColor(Colors.Grey.Darken3);
        });
    }

    private static void ComposeHotSheet(IDocumentContainer container, PartyDocumentModel model)
    {
        List<PartyMenuItem> mains = model.Menu.Where(x => x.Kind == PartyMenuItemKind.HotMain).ToList();
        List<PartyMenuItem> sides = model.Menu.Where(x => x.Kind == PartyMenuItemKind.HotSide).ToList();

        container.Page(page =>
        {
            SheetPage(page, model, "Hot food");

            page.Content().Column(column =>
            {
                column.Spacing(10);
                column.Item().Element(x => PartyHeader(x, model));

                if (mains.Count == 0)
                {
                    column.Item().Element(NoMenuNote);
                }

                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(3.2f);
                        columns.RelativeColumn(1.6f);
                        columns.RelativeColumn(3f);
                        columns.ConstantColumn(56);
                    });

                    table.Header(header =>
                    {
                        HeaderCell(header.Cell(), "Meal");
                        HeaderCell(header.Cell(), "Side");
                        HeaderCell(header.Cell(), "Tally");
                        HeaderCell(header.Cell(), "Total");
                    });

                    if (mains.Count == 0)
                    {
                        // Blank blocks to write the meals in by hand.
                        for (int i = 0; i < 5; i++)
                        {
                            table.Cell().ColumnSpan(4).Height(34).Border(0.75f).BorderColor(Colors.Grey.Darken1);
                        }

                        return;
                    }

                    foreach (PartyMenuItem main in mains)
                    {
                        uint rows = (uint)Math.Max(1, sides.Count);

                        table.Cell().RowSpan(rows).Element(BodyCell).Column(cell =>
                        {
                            cell.Item().Text(text =>
                            {
                                text.Span(main.Name.ToUpper(Uk)).Bold().FontSize(11);

                                if (main.Description is not null)
                                {
                                    text.Span($"  {main.Description}").FontSize(9).FontColor(Colors.Grey.Darken2);
                                }
                            });

                            if (main.AllergenText is not null)
                            {
                                cell.Item().PaddingTop(3).Text(main.AllergenText).FontSize(7.5f).Italic().FontColor(Colors.Grey.Darken2);
                            }
                        });

                        if (sides.Count == 0)
                        {
                            table.Cell().Element(BodyCell).Text(string.Empty);
                            table.Cell().Element(BodyCell).MinHeight(28).Text(string.Empty);
                            table.Cell().Element(BodyCell).Text(string.Empty);
                            continue;
                        }

                        foreach (PartyMenuItem side in sides)
                        {
                            table.Cell().Element(BodyCell).MinHeight(22).AlignMiddle().Text(side.Name.ToUpper(Uk)).FontSize(9).SemiBold();
                            table.Cell().Element(BodyCell).Text(string.Empty);
                            table.Cell().Element(BodyCell).Text(string.Empty);
                        }
                    }
                });

                column.Item().PaddingTop(4).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                    });

                    WriteInCell(table.Cell(), "Party bags");
                    WriteInCell(table.Cell(), "Signed");
                    WriteInCell(table.Cell(), "Total number");
                });
            });
        });
    }

    private static void ComposeColdSheet(IDocumentContainer container, PartyDocumentModel model)
    {
        List<PartyMenuItem> sandwiches = model.Menu.Where(x => x.Kind == PartyMenuItemKind.Sandwich).ToList();
        List<PartyMenuItem> allergyItems = model.Menu
            .Where(x => x.Kind is PartyMenuItemKind.Sandwich or PartyMenuItemKind.ColdExtra)
            .ToList();

        int rows = model.Booking.ExpectedChildren + SpareRows;
        int perColumn = (rows + 1) / 2;

        container.Page(page =>
        {
            SheetPage(page, model, "Cold food");

            page.Content().Column(column =>
            {
                column.Spacing(10);
                column.Item().Element(x => PartyHeader(x, model));

                if (sandwiches.Count > 0)
                {
                    column.Item().Text(text =>
                    {
                        text.Span("Sandwich choices: ").SemiBold();
                        text.Span(string.Join(" · ", sandwiches.Select(x => x.Name)));
                    });
                }
                else
                {
                    column.Item().Element(NoMenuNote);
                }

                column.Item().Row(row =>
                {
                    row.Spacing(12);
                    row.RelativeItem().Element(x => ChildRows(x, perColumn));
                    row.RelativeItem().Element(x => ChildRows(x, rows - perColumn));
                });

                if (allergyItems.Any(x => x.AllergenText is not null))
                {
                    column.Item().Border(0.75f).BorderColor(Colors.Grey.Darken1).Padding(8).Column(panel =>
                    {
                        panel.Spacing(2);
                        panel.Item().Text("ALLERGY INFORMATION").Bold().FontSize(9);

                        foreach (PartyMenuItem item in allergyItems)
                        {
                            panel.Item().Text(text =>
                            {
                                text.Span($"{item.Name}: ").SemiBold().FontSize(8);
                                text.Span(item.AllergenText ?? "No allergy information provided.").FontSize(8);
                            });
                        }
                    });
                }

                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                    });

                    WriteInCell(table.Cell(), "Signed");
                    WriteInCell(table.Cell(), "Total number");
                });
            });
        });
    }

    private static void ChildRows(IContainer container, int count)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(1.2f);
                columns.RelativeColumn();
            });

            table.Header(header =>
            {
                HeaderCell(header.Cell(), "Child's name");
                HeaderCell(header.Cell(), "Sandwich");
            });

            for (int i = 0; i < count; i++)
            {
                table.Cell().Element(BodyCell).Height(20).Text(string.Empty);
                table.Cell().Element(BodyCell).Height(20).Text(string.Empty);
            }
        });
    }

    private static void SheetPage(PageDescriptor page, PartyDocumentModel model, string title)
    {
        page.Size(PageSizes.A4);
        page.Margin(28);
        page.DefaultTextStyle(x => x.FontSize(10).FontColor(Colors.Grey.Darken4));

        page.Header().PaddingBottom(8).Row(row =>
        {
            row.RelativeItem().AlignMiddle().Text(text =>
            {
                text.Span($"{title} · ").FontSize(16).SemiBold().FontColor(model.AccentColorHex);
                text.Span($"{model.Booking.ChildName}'s party").FontSize(16).Bold();
            });

            if (model.LogoBytes is not null)
            {
                row.ConstantItem(110).Height(32).AlignRight().Image(model.LogoBytes).FitHeight();
            }
        });
    }

    // The block of party details the paper sheets start with.
    private static void PartyHeader(IContainer container, PartyDocumentModel model)
    {
        PartyBooking booking = model.Booking;

        container.Column(column =>
        {
            column.Spacing(6);

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    for (int i = 0; i < 4; i++)
                    {
                        columns.RelativeColumn();
                    }
                });

                Field(table.Cell(), "Date", RotaTime.LocalDate(booking.StartUtc).ToString("ddd d MMM yyyy", Uk));
                Field(table.Cell(), "Name", booking.ChildName);
                Field(table.Cell(), "Age", booking.ChildAgeTurning?.ToString(Uk) ?? string.Empty);
                Field(table.Cell(), "Number", booking.ExpectedChildren.ToString(Uk));

                Field(table.Cell(), "Arrival", LocalTime(booking.StartUtc));
                Field(table.Cell(), "Party type", booking.PartyType);
                Field(table.Cell(), "Eat time", booking.EatTimeUtc is DateTime eat ? LocalTime(eat) : string.Empty);
                Field(table.Cell(), "Paid", PaidText(booking));

                Field(table.Cell(), "Adults", booking.ExpectedAdults?.ToString(Uk) ?? string.Empty);
                Field(table.Cell(), "Laser tag", booking.LaserTagTimeUtc is DateTime laser ? LocalTime(laser) : string.Empty);
                Field(table.Cell(), "Room", booking.Room ?? string.Empty);
                Field(table.Cell(), "Host", model.HostName ?? string.Empty);
            });

            if (booking.AllergyNotes is not null)
            {
                column.Item().Background(Colors.Yellow.Lighten4).Border(0.75f).BorderColor(Colors.Orange.Darken1).Padding(6)
                    .Text(text =>
                    {
                        text.Span("Allergies / dietary needs: ").Bold();
                        text.Span(booking.AllergyNotes);
                    });
            }
        });
    }

    private static void Field(IContainer cell, string label, string value)
    {
        cell.Border(0.75f).BorderColor(Colors.Grey.Darken1).Padding(CellPadding).Column(column =>
        {
            column.Item().Text(label.ToUpper(Uk)).FontSize(7).SemiBold().FontColor(Colors.Grey.Darken1);
            column.Item().MinHeight(13).Text(value).FontSize(10).SemiBold();
        });
    }

    private static void HeaderCell(IContainer cell, string text) =>
        cell.Background(Colors.Grey.Lighten3).Border(0.75f).BorderColor(Colors.Grey.Darken1).Padding(CellPadding)
            .Text(text.ToUpper(Uk)).FontSize(8).Bold();

    private static IContainer BodyCell(IContainer cell) =>
        cell.Border(0.75f).BorderColor(Colors.Grey.Darken1).Padding(CellPadding);

    private static void WriteInCell(IContainer cell, string label) =>
        cell.Border(0.75f).BorderColor(Colors.Grey.Darken1).Padding(CellPadding).Height(34)
            .Text(label.ToUpper(Uk)).FontSize(8).SemiBold().FontColor(Colors.Grey.Darken1);

    private static void NoMenuNote(IContainer container) =>
        container.Text("No menu has been set up for this location yet - add it under Parties > Party Settings.")
            .FontSize(8).Italic().FontColor(Colors.Grey.Darken1);

    private static string LocalTime(DateTime utc) => RotaTime.LocalTime(utc).ToString("HH:mm", Uk);

    private static string PaidText(PartyBooking booking) => booking.PaymentStatus switch
    {
        PartyPaymentStatus.PaidInFull => "Paid in full",
        PartyPaymentStatus.DepositPaid when booking.BalanceDue is decimal due and > 0 => $"Deposit · {due.ToString("C", Uk)} due",
        PartyPaymentStatus.DepositPaid => "Deposit paid",
        _ => booking.TotalPrice is decimal total ? $"No · {total.ToString("C", Uk)} due" : "No"
    };
}

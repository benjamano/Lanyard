using System.Text.Json;
using System.Text.Json.Serialization;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Infrastructure.DTO.Parties;

// One box on a printout's artwork. Positions are millimetres from the artwork's top-left corner,
// measured on the box as it appears on the page (so a rotated box is tall and thin).
public class PartyTemplateField
{
    public PartyTemplateFieldKind Kind { get; set; }

    public double XMm { get; set; }
    public double YMm { get; set; }
    public double WidthMm { get; set; } = 60;
    public double HeightMm { get; set; } = 10;

    public double FontSize { get; set; } = 16;

    // 0 = normal; 90 = turned clockwise (reads top to bottom); 270 = turned anticlockwise (reads
    // bottom to top, like the "PARTY NAME:" strip on the feedback form).
    public int Rotation { get; set; }

    public PartyTextAlign Align { get; set; } = PartyTextAlign.Left;

    public string ColourHex { get; set; } = "#222222";

    public PartyTemplateField Clone() => (PartyTemplateField)MemberwiseClone();
}

public static class PartyTemplateFields
{
    public static readonly int[] Rotations = [0, 90, 270];

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static List<PartyTemplateField> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<PartyTemplateField>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            // A row edited by hand into something unreadable prints as if it had no boxes rather
            // than failing every printout.
            return [];
        }
    }

    public static string Serialize(IEnumerable<PartyTemplateField> fields) => JsonSerializer.Serialize(fields, Json);

    public static string Label(this PartyTemplateFieldKind kind) => kind switch
    {
        PartyTemplateFieldKind.PresentedTo => "Presented to (child's name)",
        PartyTemplateFieldKind.PartyDate => "Party date",
        PartyTemplateFieldKind.ExpiryDate => "Expiry date",
        PartyTemplateFieldKind.AuthorisedBy => "Authorised by",
        PartyTemplateFieldKind.PartyName => "Party name (\"Lily's party\")",
        PartyTemplateFieldKind.PartyHost => "Party host",
        _ => "Child's age"
    };

    // Where the boxes go on the artwork Play2Day sent (an A5 portrait voucher and feedback form),
    // measured from their files. A company with different artwork moves them on the Printouts
    // page; these only decide where a fresh template starts.
    public static PartyDocumentTemplate Default(PartyArtworkDocument document, int companyId)
    {
        List<PartyTemplateField> fields = document == PartyArtworkDocument.Voucher
            ?
            [
                Box(PartyTemplateFieldKind.PresentedTo, 67, 130.4),
                Box(PartyTemplateFieldKind.PartyDate, 67, 143.8),
                Box(PartyTemplateFieldKind.ExpiryDate, 67, 157.3),
                Box(PartyTemplateFieldKind.AuthorisedBy, 67, 170.7)
            ]
            :
            [
                new PartyTemplateField { Kind = PartyTemplateFieldKind.PartyHost, XMm = 87, YMm = 84.5, WidthMm = 50, HeightMm = 7, FontSize = 16 },
                new PartyTemplateField { Kind = PartyTemplateFieldKind.PartyName, XMm = 138, YMm = 102, WidthMm = 8.8, HeightMm = 56, FontSize = 15, Rotation = 270 }
            ];

        return new PartyDocumentTemplate
        {
            CompanyId = companyId,
            Document = document,
            FieldsJson = Serialize(fields)
        };

        static PartyTemplateField Box(PartyTemplateFieldKind kind, double x, double y) =>
            new() { Kind = kind, XMm = x, YMm = y, WidthMm = 66, HeightMm = 11, FontSize = 18 };
    }
}

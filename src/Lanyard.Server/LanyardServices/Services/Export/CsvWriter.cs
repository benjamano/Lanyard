using System.Globalization;
using System.Text;

namespace Lanyard.Application.Services.Export;

// RFC 4180 CSV cells, safe to open in Excel or Google Sheets.
public static class CsvWriter
{
    // A spreadsheet treats a cell starting with one of these as a formula, so a staff member named
    // "=HYPERLINK(...)" could run something on the payroll clerk's machine (CSV/formula injection).
    private static readonly char[] FormulaTriggers = ['=', '+', '-', '@', '\t', '\r'];

    private static readonly char[] NeedsQuoting = [',', '"', '\r', '\n'];

    // UTF-8 with a byte order mark, so Excel shows accented names correctly instead of guessing ANSI.
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    // Any text that came from a person (names, notes, labels). Neutralises formulas, then quotes.
    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string safe = Array.IndexOf(FormulaTriggers, value[0]) >= 0 ? $"'{value}" : value;

        return safe.IndexOfAny(NeedsQuoting) >= 0 ? $"\"{safe.Replace("\"", "\"\"")}\"" : safe;
    }

    // Numbers this code produced itself. Invariant culture, so a decimal is always "7.50", never "7,50".
    public static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public static string Number(decimal? value, string format) => value?.ToString(format, CultureInfo.InvariantCulture) ?? string.Empty;

    public static void AppendRow(StringBuilder builder, IEnumerable<string> cells)
    {
        builder.Append(string.Join(',', cells));
        builder.Append("\r\n");
    }

    public static byte[] ToBytes(StringBuilder builder) => [.. Utf8WithBom.GetPreamble(), .. Utf8WithBom.GetBytes(builder.ToString())];
}

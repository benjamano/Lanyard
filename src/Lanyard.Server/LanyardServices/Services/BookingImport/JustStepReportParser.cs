using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace Lanyard.Application.Services.BookingImport;

// Turns a Just Step Online "Report" (report_type=2) HTML table into bookings. Pure and static so it
// can be unit-tested against a saved page with no network.
//
// The report is grouped by day: a header row carries just "Friday 02-01-2026", then that day's
// booking rows follow until the next header. A booking row has twelve cells and an all-digits first
// cell (the party ref). "Totals for ..." rows and sub-tables (add-ons) are ignored.
public static partial class JustStepReportParser
{
    // Columns, in order, of a booking row.
    private const int ColRef = 0;
    private const int ColTime = 1;        // "Adventure Play Party 11:30AM - 01:30PM"
    private const int ColContact = 2;
    private const int ColTel = 3;
    private const int ColChild = 4;       // "Lewis Kelley,2016-01-02,Boy"
    private const int ColType = 5;        // "Party Type / Comments"
    private const int ColQty = 6;
    private const int ColTotalDue = 7;
    private const int ColPaid = 9;
    private const int ColBalance = 10;
    private const int ColBookType = 11;

    private const int MinCells = 12;

    public static IReadOnlyList<ExternalBookingDto> Parse(string html)
    {
        List<ExternalBookingDto> bookings = [];

        if (string.IsNullOrWhiteSpace(html))
        {
            return bookings;
        }

        AngleSharp.Dom.IDocument document = new HtmlParser().ParseDocument(html);

        DateOnly? currentDate = null;

        foreach (AngleSharp.Dom.IElement row in document.QuerySelectorAll("tr"))
        {
            List<string> cells = [.. row.QuerySelectorAll("td, th").Select(c => Collapse(c.TextContent))];

            if (cells.Count == 0)
            {
                continue;
            }

            string joined = Collapse(string.Join(" ", cells));

            // A day header: "Friday 02-01-2026" on its own. "Totals for Friday 02-01-2026 510 ..."
            // carries extra text and so doesn't match.
            Match header = DayHeader().Match(joined);
            if (header.Success)
            {
                currentDate = ParseUkDate(header.Groups["date"].Value);
                continue;
            }

            if (currentDate is null || cells.Count < MinCells || !AllDigits().IsMatch(cells[ColRef]))
            {
                continue;
            }

            (TimeOnly? start, TimeOnly? end) = ParseTimes(cells[ColTime]);
            (string childName, DateOnly? dob) = ParseChild(cells[ColChild]);

            string partyType = !string.IsNullOrWhiteSpace(cells[ColType]) ? cells[ColType] : cells[ColTime];

            bookings.Add(new ExternalBookingDto(
                Ref: cells[ColRef],
                PartyDate: currentDate.Value,
                StartTime: start,
                EndTime: end,
                PartyType: partyType.Trim(),
                ContactName: cells[ColContact].Trim(),
                ContactPhone: cells[ColTel].Trim(),
                ChildName: childName,
                ChildDob: dob,
                Quantity: ParseInt(cells[ColQty]),
                TotalDue: ParseMoney(cells[ColTotalDue]),
                Paid: ParseMoney(cells[ColPaid]),
                Balance: ParseMoney(cells[ColBalance]),
                IsProvisional: cells[ColBookType].Contains("Provision", StringComparison.OrdinalIgnoreCase)));
        }

        return bookings;
    }

    private static string Collapse(string? value) => WhitespaceRun().Replace(value ?? string.Empty, " ").Trim();

    // "Lewis Kelley,2016-01-02,Boy" -> name + DOB. The 1970-01-01 placeholder means "not entered".
    private static (string Name, DateOnly? Dob) ParseChild(string cell)
    {
        string[] parts = cell.Split(',', StringSplitOptions.TrimEntries);
        string name = parts.Length > 0 ? parts[0] : string.Empty;

        DateOnly? dob = null;
        if (parts.Length > 1 && DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed)
            && parsed != new DateOnly(1970, 1, 1))
        {
            dob = parsed;
        }

        return (name, dob);
    }

    // Pulls up to two clock times out of free text, e.g. "11:30AM - 01:30PM", "4.30pm to 6.30pm",
    // "Adventure Play Party10:30AM - 12:30PM". A time with no am/pm takes it from the other one.
    private static (TimeOnly? Start, TimeOnly? End) ParseTimes(string text)
    {
        MatchCollection matches = TimeToken().Matches(text);

        if (matches.Count == 0)
        {
            return (null, null);
        }

        List<(int Hour, int Minute, string? Meridiem)> raw = [];

        foreach (Match m in matches)
        {
            int hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            int minute = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);

            if (hour is < 0 or > 23 || minute > 59)
            {
                continue;
            }

            string? meridiem = m.Groups["ap"].Success ? m.Groups["ap"].Value.ToLowerInvariant() : null;
            raw.Add((hour, minute, meridiem));
        }

        if (raw.Count == 0)
        {
            return (null, null);
        }

        // Fill a missing am/pm from whichever one in the row has it.
        string? shared = raw.Select(x => x.Meridiem).FirstOrDefault(x => x is not null);

        TimeOnly? ToTime((int Hour, int Minute, string? Meridiem) t)
        {
            string? ap = t.Meridiem ?? shared;
            int hour = t.Hour;

            if (ap is not null)
            {
                hour %= 12;
                if (ap == "pm")
                {
                    hour += 12;
                }
            }

            return hour is >= 0 and <= 23 ? new TimeOnly(hour, t.Minute) : null;
        }

        TimeOnly? start = ToTime(raw[0]);
        TimeOnly? end = raw.Count > 1 ? ToTime(raw[1]) : null;

        return (start, end);
    }

    private static decimal? ParseMoney(string cell)
    {
        string cleaned = MoneyChars().Replace(cell, string.Empty);
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value) ? value : null;
    }

    private static int ParseInt(string cell)
    {
        string digits = NonDigits().Replace(cell, string.Empty);
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
    }

    private static DateOnly? ParseUkDate(string value) =>
        DateOnly.TryParseExact(value, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date) ? date : null;

    [GeneratedRegex(@"^(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)\s+(?<date>\d{2}-\d{2}-\d{4})$", RegexOptions.IgnoreCase)]
    private static partial Regex DayHeader();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex AllDigits();

    [GeneratedRegex(@"(?<h>\d{1,2})[:.](?<m>\d{2})\s*(?<ap>am|pm)?", RegexOptions.IgnoreCase)]
    private static partial Regex TimeToken();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"[^0-9.\-]")]
    private static partial Regex MoneyChars();

    [GeneratedRegex(@"[^0-9]")]
    private static partial Regex NonDigits();
}

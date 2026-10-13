using System.Globalization;
using System.Text;

namespace Lanyard.Application.Services.Scheduling;

public record IcsEvent(
    string Uid,
    DateTime StartUtc,
    DateTime EndUtc,
    DateTime LastModifiedUtc,
    string Summary,
    string? Location,
    string? Description,
    string? Url);

// Writes an RFC 5545 calendar for a subscribed (read-only) feed. Hand-written rather than a
// library because a feed of timed events is all it ever has to say: no recurrence, no invites,
// no attendees.
//
// Times are written in UTC ("...Z"), so there's no VTIMEZONE to generate; calendar apps show them
// in the device's own zone. X-WR-TIMEZONE only hints the venue zone to apps that use it to pick a
// display zone for the calendar.
//
// The output depends only on its input - DTSTAMP is each event's last change, never "now" - so an
// unchanged rota gives byte-identical output and the feed's ETag lets pollers get a 304.
public static class IcsCalendarWriter
{
    // RFC 5545 3.1: lines longer than 75 octets (excluding the CRLF) are folded.
    private const int MaxLineOctets = 75;

    public static string Write(string calendarName, IEnumerable<IcsEvent> events)
    {
        StringBuilder sb = new();

        AppendLine(sb, "BEGIN:VCALENDAR");
        AppendLine(sb, "VERSION:2.0");
        AppendLine(sb, "PRODID:-//Lanyard//Shift calendar//EN");
        AppendLine(sb, "CALSCALE:GREGORIAN");
        AppendLine(sb, "METHOD:PUBLISH");
        AppendLine(sb, $"X-WR-CALNAME:{Escape(calendarName)}");
        AppendLine(sb, "X-WR-TIMEZONE:Europe/London");
        // How often to re-fetch. Apple and Outlook roughly honour these; Google ignores both and
        // polls on its own schedule (anything up to a day).
        AppendLine(sb, "REFRESH-INTERVAL;VALUE=DURATION:PT1H");
        AppendLine(sb, "X-PUBLISHED-TTL:PT1H");

        foreach (IcsEvent ev in events)
        {
            AppendLine(sb, "BEGIN:VEVENT");
            AppendLine(sb, $"UID:{Escape(ev.Uid)}");
            AppendLine(sb, $"DTSTAMP:{FormatUtc(ev.LastModifiedUtc)}");
            AppendLine(sb, $"LAST-MODIFIED:{FormatUtc(ev.LastModifiedUtc)}");
            AppendLine(sb, $"DTSTART:{FormatUtc(ev.StartUtc)}");
            AppendLine(sb, $"DTEND:{FormatUtc(ev.EndUtc)}");
            AppendLine(sb, $"SUMMARY:{Escape(ev.Summary)}");

            if (!string.IsNullOrWhiteSpace(ev.Location))
            {
                AppendLine(sb, $"LOCATION:{Escape(ev.Location)}");
            }

            if (!string.IsNullOrWhiteSpace(ev.Description))
            {
                AppendLine(sb, $"DESCRIPTION:{Escape(ev.Description)}");
            }

            if (!string.IsNullOrWhiteSpace(ev.Url))
            {
                // URL is a URI value, not TEXT, so it isn't escaped.
                AppendLine(sb, $"URL:{ev.Url}");
            }

            AppendLine(sb, "STATUS:CONFIRMED");
            AppendLine(sb, "TRANSP:OPAQUE");
            AppendLine(sb, "END:VEVENT");
        }

        AppendLine(sb, "END:VCALENDAR");

        return sb.ToString();
    }

    public static string FormatUtc(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    // TEXT escaping (RFC 5545 3.3.11). Bare CRs are dropped so CRLF and LF both become one \n.
    public static string Escape(string value)
    {
        StringBuilder sb = new(value.Length);

        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case ';': sb.Append("\\;"); break;
                case ',': sb.Append("\\,"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                default:
                    // Other control characters aren't allowed in TEXT.
                    if (!char.IsControl(c))
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }

        return sb.ToString();
    }

    // Folds at 75 octets of UTF-8, never inside a character (or a surrogate pair), with each
    // continuation line starting with a single space that counts towards its 75.
    public static string Fold(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= MaxLineOctets)
        {
            return line;
        }

        StringBuilder sb = new();
        int octets = 0;
        int limit = MaxLineOctets;

        for (int i = 0; i < line.Length; i++)
        {
            int charLength = char.IsHighSurrogate(line[i]) && i + 1 < line.Length ? 2 : 1;
            int charOctets = Encoding.UTF8.GetByteCount(line.AsSpan(i, charLength));

            if (octets + charOctets > limit)
            {
                sb.Append("\r\n ");
                octets = 0;
                limit = MaxLineOctets - 1;
            }

            sb.Append(line, i, charLength);
            octets += charOctets;
            i += charLength - 1;
        }

        return sb.ToString();
    }

    private static void AppendLine(StringBuilder sb, string line) => sb.Append(Fold(line)).Append("\r\n");
}

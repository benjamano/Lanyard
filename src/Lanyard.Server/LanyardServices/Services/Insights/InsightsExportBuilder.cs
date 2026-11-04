using Lanyard.Application.Services.Export;
using Lanyard.Infrastructure.DTO.Insights;
using System.Globalization;
using System.Text;

namespace Lanyard.Application.Services.Insights;

// One CSV row per venue hour that was open or had anything in it. Deliberately aggregate only:
// no staff names and no pay, so the file is safe to pass around.
public static class InsightsExportBuilder
{
    public static readonly string[] Header =
    [
        "Date", "Day", "Hour", "Open", "Part of day", "Games", "Players", "Clocked hours", "Rota hours", "Games per staff hour"
    ];

    public static byte[] Build(InsightsSummary summary)
    {
        StringBuilder csv = new();
        CsvWriter.AppendRow(csv, Header);

        foreach (InsightsSlot slot in summary.Figures.Slots)
        {
            decimal? rate = slot.ClockedHours > 0 ? slot.Games / slot.ClockedHours : null;

            CsvWriter.AppendRow(csv,
            [
                slot.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                slot.Date.DayOfWeek.ToString(),
                $"{slot.Hour:00}:00",
                slot.IsOpen ? "Yes" : "No",
                CsvWriter.Text(slot.Daypart),
                CsvWriter.Number(slot.Games),
                CsvWriter.Number(slot.Players),
                CsvWriter.Number(slot.ClockedHours, "0.00"),
                CsvWriter.Number(slot.RotaHours, "0.00"),
                CsvWriter.Number(rate, "0.00")
            ]);
        }

        return CsvWriter.ToBytes(csv);
    }

    public static string FileName(InsightsSummary summary)
    {
        string location = new([.. summary.LocationName.Select(c => char.IsLetterOrDigit(c) ? c : '-')]);
        return $"Insights {location.Trim('-')} {summary.From:yyyy-MM-dd} to {summary.To:yyyy-MM-dd}.csv";
    }
}

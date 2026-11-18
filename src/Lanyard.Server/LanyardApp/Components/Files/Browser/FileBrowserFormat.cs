using System.Globalization;
using Lanyard.Infrastructure.DTO.Files;
using Lanyard.Infrastructure.Enum;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Lanyard.App.Components.Files.Browser;

/// <summary>How Files shows things: icons per type, venue-local dates and "3 days ago".</summary>
public static class FileBrowserFormat
{
    private static readonly CultureInfo Uk = new("en-GB");

    public static string DownloadUrl(Guid fileId) => $"api/files/download/{fileId}";

    // One size: CSS scales the svg up for grid tiles and previews (not every icon comes in 48).
    public static Icon IconFor(FileTypeGroup group, string fileName, bool large = false)
    {
        _ = large;
        string extension = Path.GetExtension(fileName).ToLowerInvariant();

        return group switch
        {
            FileTypeGroup.Image => new Icons.Regular.Size24.Image(),
            FileTypeGroup.Video => new Icons.Regular.Size24.Video(),
            FileTypeGroup.Audio => new Icons.Regular.Size24.MusicNote2(),
            FileTypeGroup.Document when extension == ".pdf" => new Icons.Regular.Size24.DocumentPdf(),
            FileTypeGroup.Document when extension is ".xls" or ".xlsx" or ".csv" or ".ods" => new Icons.Regular.Size24.DocumentTable(),
            FileTypeGroup.Document => new Icons.Regular.Size24.DocumentText(),
            _ when extension == ".zip" => new Icons.Regular.Size24.FolderZip(),
            _ => new Icons.Regular.Size24.Document(),
        };
    }

    /// <summary>A CSS hook for the type's accent colour (see FileBrowser.razor.css).</summary>
    public static string TypeClass(FileTypeGroup group) => group switch
    {
        FileTypeGroup.Image => "is-image",
        FileTypeGroup.Video => "is-video",
        FileTypeGroup.Audio => "is-audio",
        FileTypeGroup.Document => "is-document",
        _ => "is-other",
    };

    public static string GroupLabel(FileTypeGroup group) => group switch
    {
        FileTypeGroup.Image => "Images",
        FileTypeGroup.Video => "Videos",
        FileTypeGroup.Audio => "Audio",
        FileTypeGroup.Document => "Documents",
        _ => "Other",
    };

    public static DateTime Local(DateTime utc) => RotaTime.ToVenueLocal(utc);

    /// <summary>"18 Nov 2026, 14:05", in UK time.</summary>
    public static string When(DateTime utc) => Local(utc).ToString("d MMM yyyy, HH:mm", Uk);

    public static string Date(DateTime utc) => Local(utc).ToString("d MMM yyyy", Uk);

    /// <summary>"just now", "5 minutes ago", "yesterday", "3 days ago", then the date.</summary>
    public static string Ago(DateTime utc, DateTime? nowUtc = null)
    {
        DateTime now = nowUtc ?? DateTime.UtcNow;
        TimeSpan age = now - utc;

        if (age < TimeSpan.FromMinutes(1))
            return "just now";

        if (age < TimeSpan.FromHours(1))
            return Plural((int)age.TotalMinutes, "minute") + " ago";

        if (age < TimeSpan.FromDays(1) && Local(utc).Date == Local(now).Date)
            return Plural((int)age.TotalHours, "hour") + " ago";

        int days = (Local(now).Date - Local(utc).Date).Days;

        return days switch
        {
            <= 1 => "yesterday",
            < 7 => $"{days} days ago",
            _ => Date(utc),
        };
    }

    /// <summary>"Deletes today", "Deletes tomorrow", "Deletes in 12 days".</summary>
    public static string PurgeIn(DateTime purgeAfterUtc, DateTime? nowUtc = null)
    {
        int days = (int)Math.Ceiling((purgeAfterUtc - (nowUtc ?? DateTime.UtcNow)).TotalDays);

        return days switch
        {
            <= 0 => "Deletes today",
            1 => "Deletes tomorrow",
            _ => $"Deletes in {days} days",
        };
    }

    public static string Duration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    public static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    public static string FolderSummary(FolderNode folder)
    {
        int items = folder.FileCount + folder.FolderCount;
        return items == 0 ? "Empty" : Plural(items, "item");
    }
}

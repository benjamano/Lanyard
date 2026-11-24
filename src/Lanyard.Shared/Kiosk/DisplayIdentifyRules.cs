using Lanyard.Shared.DTO;

namespace Lanyard.Shared.Kiosk;

/// <summary>
/// Shared rules for "Identify displays", so the kiosk's overlay and the server's pages number and
/// describe screens the same way. Screens are numbered from 1 (index + 1), as everywhere else in
/// Lanyard that says "Screen N".
/// </summary>
public static class DisplayIdentifyRules
{
    /// <summary>How long the overlay stays up. Fixed for now; pressing Identify again restarts it.</summary>
    public const int DefaultDurationSeconds = 8;

    public const int MinDurationSeconds = 3;
    public const int MaxDurationSeconds = 30;

    /// <summary>Most screens a reply may describe; the server refuses more.</summary>
    public const int MaxDisplays = 16;

    public const int MaxDeviceNameLength = 100;
    public const int MaxProgramNameLength = 100;
    public const int MaxErrorLength = 300;

    /// <summary>The kiosk release that first handles <c>ClientHubMethods.IdentifyDisplays</c>.</summary>
    public const string MinimumClientVersion = "1.0.49";

    public static int ScreenNumber(int index) => index + 1;

    public static int ClampDuration(int seconds)
    {
        return seconds <= 0 ? DefaultDurationSeconds : Math.Clamp(seconds, MinDurationSeconds, MaxDurationSeconds);
    }

    /// <summary>"Screen 2", as the overlay's caption and in the result list.</summary>
    public static string ScreenTitle(int index) => $"Screen {ScreenNumber(index)}";

    /// <summary>"\\.\DISPLAY2 · 1920×1080 · Main screen", the overlay's second line.</summary>
    public static string Describe(string deviceName, int width, int height, bool isPrimary)
    {
        string text = $"{deviceName} · {width}×{height}";

        return isPrimary ? $"{text} · Main screen" : text;
    }

    /// <summary>"Screen 2 · \\.\DISPLAY2 (1920x1080)", for display pickers.</summary>
    public static string PickerLabel(int index, string deviceName, int width, int height)
    {
        return $"{ScreenTitle(index)} · {deviceName} ({width}x{height})";
    }

    /// <summary>The program line on the overlay.</summary>
    public static string ProgramLine(string? programName)
    {
        return string.IsNullOrWhiteSpace(programName) ? "No program assigned" : $"Program: {programName.Trim()}";
    }

    /// <summary>The program for a screen index from the request's labels, or null.</summary>
    public static string? ProgramFor(IEnumerable<DisplayIdentifyLabelDTO>? labels, int index)
    {
        return labels?.FirstOrDefault(x => x.DisplayIndex == index)?.ProgramName;
    }

    public static string? Truncate(string? text, int maxLength)
    {
        if (text == null || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength];
    }

    /// <summary>
    /// Why a kiosk's reply can't be used, or null when it can. The kiosk is trusted only so far,
    /// so the reply is matched to its request and its size is capped.
    /// </summary>
    public static string? ValidateReply(DisplayIdentifyResultDTO? reply, Guid requestId)
    {
        if (reply == null)
        {
            return "The kiosk sent back an empty reply.";
        }

        if (reply.RequestId != requestId)
        {
            return "The kiosk's reply was for a different request.";
        }

        if (reply.Displays == null || reply.Displays.Count == 0)
        {
            return "The kiosk didn't report any screens.";
        }

        if (reply.Displays.Count > MaxDisplays)
        {
            return "The kiosk reported more screens than expected.";
        }

        if (reply.Displays.Any(x => x.Index < 0 || x.Index >= MaxDisplays))
        {
            return "The kiosk reported a screen with an invalid number.";
        }

        if (reply.Displays.Select(x => x.Index).Distinct().Count() != reply.Displays.Count)
        {
            return "The kiosk reported the same screen twice.";
        }

        return null;
    }
}

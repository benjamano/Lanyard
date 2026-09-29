using System.Text.RegularExpressions;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Notifications;

// What the browser reports about itself, gathered by lanyardInstall.js.
public record DeviceReport(string? UserAgent, int MaxTouchPoints, bool IsStandalone);

public record DeviceDescription(DevicePlatform Platform, string Browser, string Label, Version? IosVersion)
{
    // Web push on iPhone and iPad arrived in iOS 16.4, and only for Home Screen apps.
    public static readonly Version FirstIosWithPush = new(16, 4);

    public bool IsApple => Platform is DevicePlatform.IPhone or DevicePlatform.IPad;

    public bool IsPhoneOrTablet => Platform is DevicePlatform.IPhone or DevicePlatform.IPad or DevicePlatform.Android;

    public bool IosSupportsPush => IosVersion is null || IosVersion >= FirstIosWithPush;
}

// Turns a user agent into "Safari on iPhone" for the device list and into a platform for the
// install prompt. Deliberately rough: it only ever chooses which instructions to show and what a
// device is called, never what anyone is allowed to do.
public static partial class DeviceDescriptor
{
    public static DeviceDescription Describe(DeviceReport report)
    {
        string ua = report.UserAgent ?? string.Empty;

        DevicePlatform platform = PlatformOf(ua, report.MaxTouchPoints);
        string browser = BrowserOf(ua, platform);
        Version? iosVersion = platform is DevicePlatform.IPhone or DevicePlatform.IPad ? IosVersionOf(ua) : null;

        string where = platform switch
        {
            DevicePlatform.IPhone => "iPhone",
            DevicePlatform.IPad => "iPad",
            DevicePlatform.Android => "Android",
            DevicePlatform.Desktop => DesktopOsOf(ua),
            _ => "this device"
        };

        string label = report.IsStandalone ? $"Lanyard app on {where}" : $"{browser} on {where}";

        return new DeviceDescription(platform, browser, label, iosVersion);
    }

    private static DevicePlatform PlatformOf(string ua, int maxTouchPoints)
    {
        if (ua.Contains("iPad", StringComparison.Ordinal))
        {
            return DevicePlatform.IPad;
        }

        if (ua.Contains("iPhone", StringComparison.Ordinal) || ua.Contains("iPod", StringComparison.Ordinal))
        {
            return DevicePlatform.IPhone;
        }

        if (ua.Contains("Android", StringComparison.Ordinal))
        {
            return DevicePlatform.Android;
        }

        // Since iPadOS 13 Safari asks for desktop sites and reports itself as a Mac. A Mac has no
        // touch screen, so a "Mac" with touch points is an iPad.
        if (ua.Contains("Macintosh", StringComparison.Ordinal) && maxTouchPoints > 1)
        {
            return DevicePlatform.IPad;
        }

        if (ua.Contains("Windows", StringComparison.Ordinal) || ua.Contains("Macintosh", StringComparison.Ordinal)
            || ua.Contains("CrOS", StringComparison.Ordinal) || ua.Contains("Linux", StringComparison.Ordinal)
            || ua.Contains("X11", StringComparison.Ordinal))
        {
            return DevicePlatform.Desktop;
        }

        return DevicePlatform.Other;
    }

    // Order matters: Edge, Samsung and Chrome on iOS all also say "Safari", and Edge says "Chrome".
    private static string BrowserOf(string ua, DevicePlatform platform)
    {
        if (ua.Contains("EdgiOS", StringComparison.Ordinal) || ua.Contains("EdgA/", StringComparison.Ordinal) || ua.Contains("Edg/", StringComparison.Ordinal))
        {
            return "Edge";
        }

        if (ua.Contains("SamsungBrowser", StringComparison.Ordinal))
        {
            return "Samsung Internet";
        }

        if (ua.Contains("FxiOS", StringComparison.Ordinal) || ua.Contains("Firefox/", StringComparison.Ordinal))
        {
            return "Firefox";
        }

        if (ua.Contains("CriOS", StringComparison.Ordinal) || ua.Contains("Chrome/", StringComparison.Ordinal))
        {
            return "Chrome";
        }

        if (ua.Contains("Safari/", StringComparison.Ordinal) || platform is DevicePlatform.IPhone or DevicePlatform.IPad)
        {
            return "Safari";
        }

        return "Browser";
    }

    private static string DesktopOsOf(string ua)
    {
        if (ua.Contains("Windows", StringComparison.Ordinal))
        {
            return "Windows";
        }

        if (ua.Contains("Macintosh", StringComparison.Ordinal))
        {
            return "Mac";
        }

        if (ua.Contains("CrOS", StringComparison.Ordinal))
        {
            return "ChromeOS";
        }

        return "Linux";
    }

    // "iPhone OS 17_4_1" / "CPU OS 16_3". An iPad posing as a Mac reports the macOS Safari
    // version instead, so no number is found and the device is assumed to be current.
    private static Version? IosVersionOf(string ua)
    {
        Match match = IosVersionPattern().Match(ua);

        if (!match.Success)
        {
            return null;
        }

        int major = int.Parse(match.Groups[1].Value);
        int minor = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;

        return new Version(major, minor);
    }

    [GeneratedRegex(@"OS (\d+)(?:_(\d+))?(?:_\d+)? like Mac OS X")]
    private static partial Regex IosVersionPattern();
}

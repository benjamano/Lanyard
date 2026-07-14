namespace Lanyard.Infrastructure.DTO.VideoDevices;

/// <summary>
/// Encodes which client owns a selected video device as "{clientId}::{deviceName}".
/// Values without the separator are legacy plain device names, matched against the
/// kiosk machine's own devices.
/// </summary>
public static class VideoDeviceParameterValue
{
    public const string Separator = "::";

    public static string Format(Guid clientId, string deviceName)
    {
        return $"{clientId}{Separator}{deviceName}";
    }

    public static bool TryParseRemote(string? value, out Guid sourceClientId, out string deviceName)
    {
        sourceClientId = Guid.Empty;
        deviceName = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        int separatorIndex = value.IndexOf(Separator, StringComparison.Ordinal);

        if (separatorIndex <= 0)
        {
            return false;
        }

        if (!Guid.TryParse(value[..separatorIndex], out sourceClientId))
        {
            return false;
        }

        deviceName = value[(separatorIndex + Separator.Length)..];

        return true;
    }
}

using System.Text.Json;

namespace Lanyard.App.Components.Kiosk.Widgets;

public static class DashboardWidgetConfigs
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string MusicClientModeFixed = "fixed";
    private const string MusicClientModeUser = "user";

    public sealed class TextAreaWidgetConfig
    {
        public bool SaveToLocalStorage { get; set; }
    }

    public sealed class MusicControlsWidgetConfig
    {
        public string ClientMode { get; set; } = MusicClientModeFixed;
        public Guid? FixedClientId { get; set; }
    }

    public static TextAreaWidgetConfig ParseTextAreaConfig(string? configJson)
    {
        if (TryDeserialize<TextAreaWidgetConfig>(configJson, out TextAreaWidgetConfig? parsed) && parsed is not null)
        {
            return new TextAreaWidgetConfig
            {
                SaveToLocalStorage = parsed.SaveToLocalStorage
            };
        }

        return new TextAreaWidgetConfig
        {
            SaveToLocalStorage = false
        };
    }

    public static string SerializeTextAreaConfig(TextAreaWidgetConfig config)
    {
        return JsonSerializer.Serialize(config, JsonOptions);
    }

    public static MusicControlsWidgetConfig ParseMusicControlsConfig(string? configJson)
    {
        if (Guid.TryParse(configJson, out Guid fixedClientId))
        {
            return new MusicControlsWidgetConfig
            {
                ClientMode = MusicClientModeFixed,
                FixedClientId = fixedClientId
            };
        }

        if (TryDeserialize<MusicControlsWidgetConfig>(configJson, out MusicControlsWidgetConfig? parsed) && parsed is not null)
        {
            string normalizedMode = NormalizeMusicClientMode(parsed.ClientMode);
            return new MusicControlsWidgetConfig
            {
                ClientMode = normalizedMode,
                FixedClientId = parsed.FixedClientId
            };
        }

        return new MusicControlsWidgetConfig
        {
            ClientMode = MusicClientModeFixed,
            FixedClientId = null
        };
    }

    public static string SerializeMusicControlsConfig(MusicControlsWidgetConfig config)
    {
        MusicControlsWidgetConfig normalized = new()
        {
            ClientMode = NormalizeMusicClientMode(config.ClientMode),
            FixedClientId = config.FixedClientId
        };

        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    public static bool IsMusicClientModeUser(string? mode)
    {
        return string.Equals(mode, MusicClientModeUser, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMusicClientModeFixed(string? mode)
    {
        return !IsMusicClientModeUser(mode);
    }

    private static string NormalizeMusicClientMode(string? mode)
    {
        return IsMusicClientModeUser(mode) ? MusicClientModeUser : MusicClientModeFixed;
    }

    private static bool TryDeserialize<TConfig>(string? configJson, out TConfig? config)
    {
        config = default;

        if (string.IsNullOrWhiteSpace(configJson))
        {
            return false;
        }

        try
        {
            config = JsonSerializer.Deserialize<TConfig>(configJson, JsonOptions);
            return config is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

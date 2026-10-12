using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;

namespace Lanyard.Application.Services.Dmx.Ofl;

// Reads one Open Fixture Library fixture file (https://github.com/OpenLightingProject/open-fixture-library,
// docs/fixture-format.md) into the modes and channel roles Lanyard needs. The JSON is untrusted -
// it comes from an uploaded file as well as the bundled snapshot - so sizes, counts and depth are
// capped, every string is trimmed and length-limited, and anything malformed is a Fail, never an
// exception.
//
// Not supported in v1, and reported per mode rather than failing the whole fixture: pixel/matrix
// modes (an "insert" block, or a channel that only exists as a template for each pixel).
public partial class OflFixtureParser : IOflFixtureParser
{
    public const int MaxFileBytes = 512 * 1024;
    public const int MaxModes = 256;
    public const int MaxAvailableChannels = 512;
    public const int MaxCapabilitiesPerChannel = 1024;
    public const int MaxNameLength = 100;
    private const int UniverseSize = 512;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = 32,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    private static readonly Dictionary<string, DmxChannelRole> ColorIntensityRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Red"] = DmxChannelRole.Red,
        ["Green"] = DmxChannelRole.Green,
        ["Blue"] = DmxChannelRole.Blue,
        ["White"] = DmxChannelRole.White,
        ["Warm White"] = DmxChannelRole.WarmWhite,
        ["Cold White"] = DmxChannelRole.ColdWhite,
        ["Amber"] = DmxChannelRole.Amber,
        ["Lime"] = DmxChannelRole.Lime,
        ["UV"] = DmxChannelRole.Uv,
        ["Indigo"] = DmxChannelRole.Indigo,
        ["Cyan"] = DmxChannelRole.Cyan,
        ["Magenta"] = DmxChannelRole.Magenta,
        ["Yellow"] = DmxChannelRole.Yellow,
    };

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    public Result<OflParsedFixture> Parse(Stream json, string? oflKey, string? manufacturerName)
    {
        try
        {
            using MemoryStream buffer = new();
            byte[] chunk = new byte[16 * 1024];
            int read;

            while ((read = json.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxFileBytes)
                {
                    return Result<OflParsedFixture>.Fail($"That file is too big for a fixture definition (the limit is {MaxFileBytes / 1024} KB).");
                }

                buffer.Write(chunk, 0, read);
            }

            using JsonDocument document = JsonDocument.Parse(buffer.ToArray(), DocumentOptions);

            return Parse(document.RootElement, oflKey, manufacturerName);
        }
        catch (JsonException)
        {
            return Result<OflParsedFixture>.Fail("That file isn't a valid Open Fixture Library fixture (it isn't readable JSON).");
        }
    }

    public Result<OflParsedFixture> Parse(JsonElement fixture, string? oflKey, string? manufacturerName)
    {
        try
        {
            return ParseFixture(fixture, oflKey, manufacturerName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            // TryGet* everywhere below; this only catches a shape we didn't anticipate.
            return Result<OflParsedFixture>.Fail("That file isn't a valid Open Fixture Library fixture.");
        }
    }

    private static Result<OflParsedFixture> ParseFixture(JsonElement fixture, string? oflKey, string? manufacturerName)
    {
        if (fixture.ValueKind != JsonValueKind.Object)
        {
            return Result<OflParsedFixture>.Fail("That file isn't a valid Open Fixture Library fixture.");
        }

        string? model = ReadString(fixture, "name");

        if (string.IsNullOrEmpty(model))
        {
            return Result<OflParsedFixture>.Fail("The fixture has no name.");
        }

        // Pixel fixtures may define every channel as a template, with no availableChannels at all.
        bool hasChannels = fixture.TryGetProperty("availableChannels", out JsonElement availableChannels) && availableChannels.ValueKind == JsonValueKind.Object;
        bool hasTemplates = fixture.TryGetProperty("templateChannels", out JsonElement templateCheck) && templateCheck.ValueKind == JsonValueKind.Object;

        if (!hasChannels && !hasTemplates)
        {
            return Result<OflParsedFixture>.Fail("The fixture has no channels.");
        }

        if (!fixture.TryGetProperty("modes", out JsonElement modesElement) || modesElement.ValueKind != JsonValueKind.Array)
        {
            return Result<OflParsedFixture>.Fail("The fixture has no modes.");
        }

        if (modesElement.GetArrayLength() is 0 or > MaxModes)
        {
            return Result<OflParsedFixture>.Fail($"A fixture must have between 1 and {MaxModes} modes.");
        }

        Dictionary<string, JsonElement> wheels = ReadObject(fixture, "wheels");
        Dictionary<string, ChannelInfo> channels = new(StringComparer.Ordinal);
        int channelCount = 0;

        IEnumerable<JsonProperty> availableChannelList = hasChannels ? availableChannels.EnumerateObject() : [];

        foreach (JsonProperty channel in availableChannelList)
        {
            if (++channelCount > MaxAvailableChannels)
            {
                return Result<OflParsedFixture>.Fail($"A fixture can't have more than {MaxAvailableChannels} channels.");
            }

            Result<ChannelInfo> info = ReadChannel(channel.Name, channel.Value, wheels);

            if (!info.IsSuccess)
            {
                return Result<OflParsedFixture>.Fail(info.Error!);
            }

            channels[channel.Name] = info.Data!;
        }

        // Fine aliases ("Pan fine") and switching aliases are addressable in a mode like real channels.
        Dictionary<string, AliasInfo> aliases = new(StringComparer.Ordinal);

        foreach (ChannelInfo channel in channels.Values)
        {
            for (int i = 0; i < channel.FineAliases.Count; i++)
            {
                aliases.TryAdd(channel.FineAliases[i], new AliasInfo(channel.Key, (byte)(i + 1)));
            }

            foreach (KeyValuePair<string, string> switching in channel.SwitchingAliases)
            {
                aliases.TryAdd(switching.Key, new AliasInfo(switching.Value, 0));
            }
        }

        // Pixel fixtures define "Red $pixelKey" once; a fixed mode can still list the resolved
        // names ("Red 1", "Red 2"), which is just an ordinary channel.
        List<TemplateInfo> templates = [];

        if (fixture.TryGetProperty("templateChannels", out JsonElement templateChannels) && templateChannels.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty template in templateChannels.EnumerateObject())
            {
                if (++channelCount > MaxAvailableChannels)
                {
                    return Result<OflParsedFixture>.Fail($"A fixture can't have more than {MaxAvailableChannels} channels.");
                }

                Result<ChannelInfo> info = ReadChannel(template.Name, template.Value, wheels);

                if (!info.IsSuccess)
                {
                    return Result<OflParsedFixture>.Fail(info.Error!);
                }

                templates.Add(new TemplateInfo(
                    TemplatePattern(template.Name),
                    template.Name,
                    info.Data!,
                    info.Data!.FineAliases.Select((alias, i) => (TemplatePattern(alias), (byte)(i + 1))).ToList()));
            }
        }

        List<OflParsedMode> modes = [];

        foreach (JsonElement mode in modesElement.EnumerateArray())
        {
            if (mode.ValueKind != JsonValueKind.Object)
            {
                return Result<OflParsedFixture>.Fail("One of the fixture's modes isn't valid.");
            }

            modes.Add(ReadMode(mode, channels, aliases, templates));
        }

        string? revision = fixture.TryGetProperty("meta", out JsonElement meta) && meta.ValueKind == JsonValueKind.Object
            ? ReadString(meta, "lastModifyDate", 50)
            : null;

        return Result<OflParsedFixture>.Ok(new OflParsedFixture(
            Clean(oflKey, 200) ?? string.Empty,
            Clean(manufacturerName) ?? "Unknown manufacturer",
            model,
            revision,
            modes));
    }

    private static OflParsedMode ReadMode(JsonElement mode, Dictionary<string, ChannelInfo> channels, Dictionary<string, AliasInfo> aliases, List<TemplateInfo> templates)
    {
        string name = ReadString(mode, "name") ?? "Unnamed";
        string? shortName = ReadString(mode, "shortName", 50);

        if (!mode.TryGetProperty("channels", out JsonElement modeChannels) || modeChannels.ValueKind != JsonValueKind.Array)
        {
            return Unsupported(name, shortName, 0, "This mode has no channel list.");
        }

        int count = modeChannels.GetArrayLength();

        if (count == 0)
        {
            return Unsupported(name, shortName, 0, "This mode has no channels.");
        }

        if (count > UniverseSize)
        {
            return Unsupported(name, shortName, count, $"This mode needs {count} channels, more than one D M X universe.");
        }

        List<(string? Key, int Offset)> entries = [];
        int offset = 0;

        foreach (JsonElement entry in modeChannels.EnumerateArray())
        {
            switch (entry.ValueKind)
            {
                case JsonValueKind.Null:
                    entries.Add((null, offset));
                    break;
                case JsonValueKind.String:
                    entries.Add((entry.GetString(), offset));
                    break;
                default:
                    // An "insert" block repeats template channels per pixel.
                    return Unsupported(name, shortName, count, "Pixel (matrix) modes aren't supported yet.");
            }

            offset++;
        }

        // Where each channel key sits in this mode, for resolving fine channels to their coarse one.
        Dictionary<string, int> offsetByKey = new(StringComparer.Ordinal);
        foreach ((string? key, int position) in entries)
        {
            if (key != null)
            {
                offsetByKey.TryAdd(key, position);
            }
        }

        List<OflParsedChannel> parsed = [];

        foreach ((string? key, int position) in entries)
        {
            if (key == null)
            {
                parsed.Add(new OflParsedChannel(position, "(unused)", DmxChannelRole.NoFunction, null, 0, 0, []));
                continue;
            }

            if (channels.TryGetValue(key, out ChannelInfo? channel))
            {
                parsed.Add(new OflParsedChannel(position, Clean(key)!, channel.Role, null, 0, channel.DefaultValue, channel.ColorRanges));
                continue;
            }

            if (aliases.TryGetValue(key, out AliasInfo? alias) && channels.TryGetValue(alias.ChannelKey, out ChannelInfo? target))
            {
                if (alias.FineLevel > 0)
                {
                    int? coarseOffset = offsetByKey.TryGetValue(alias.ChannelKey, out int coarse) ? coarse : null;
                    parsed.Add(new OflParsedChannel(position, Clean(key)!, target.Role, coarseOffset, alias.FineLevel, 0, []));
                }
                else
                {
                    // A switching channel: use whatever it's switched to by default.
                    parsed.Add(new OflParsedChannel(position, Clean(key)!, target.Role, null, 0, target.DefaultValue, target.ColorRanges));
                }

                continue;
            }

            if (TryResolveTemplate(key, templates, offsetByKey, position) is OflParsedChannel resolved)
            {
                parsed.Add(resolved);
                continue;
            }

            return Unsupported(name, shortName, count, $"This mode uses a channel (\"{Clean(key, 40)}\") the fixture doesn't define.");
        }

        // A fine channel whose coarse channel isn't in this mode is just a plain channel here.
        parsed = parsed
            .Select(c => c.FineLevel > 0 && c.CoarseOffset == null ? c with { FineLevel = 0 } : c)
            .ToList();

        return new OflParsedMode(name, shortName, count, true, null, parsed);
    }

    // "Red 1" against the template "Red $pixelKey" (or a fine alias such as "Red $pixelKey fine").
    private static OflParsedChannel? TryResolveTemplate(string key, List<TemplateInfo> templates, Dictionary<string, int> offsetByKey, int position)
    {
        // Fine aliases first: "Pan A fine" would otherwise match "Pan $pixelKey" with pixel "A fine".
        foreach (TemplateInfo template in templates)
        {
            foreach ((Regex pattern, byte fineLevel) in template.FinePatterns)
            {
                Match fine = pattern.Match(key);

                if (fine.Success)
                {
                    string coarseKey = template.Key.Replace("$pixelKey", fine.Groups[1].Value, StringComparison.Ordinal);
                    int? coarseOffset = offsetByKey.TryGetValue(coarseKey, out int coarse) ? coarse : null;

                    return new OflParsedChannel(position, Clean(key)!, template.Channel.Role, coarseOffset, fineLevel, 0, []);
                }
            }
        }

        foreach (TemplateInfo template in templates)
        {
            if (template.Pattern.IsMatch(key))
            {
                return new OflParsedChannel(position, Clean(key)!, template.Channel.Role, null, 0, template.Channel.DefaultValue, template.Channel.ColorRanges);
            }
        }

        return null;
    }

    private static Regex TemplatePattern(string templateKey) =>
        new("^" + Regex.Escape(templateKey).Replace("\\$pixelKey", "(.+?)", StringComparison.Ordinal) + "$",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static OflParsedMode Unsupported(string name, string? shortName, int count, string reason) =>
        new(name, shortName, count, false, reason, []);

    private static Result<ChannelInfo> ReadChannel(string key, JsonElement channel, Dictionary<string, JsonElement> wheels)
    {
        if (channel.ValueKind != JsonValueKind.Object)
        {
            return Result<ChannelInfo>.Fail($"Channel \"{Clean(key)}\" isn't valid.");
        }

        List<string> fineAliases = [];
        if (channel.TryGetProperty("fineChannelAliases", out JsonElement fine) && fine.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement alias in fine.EnumerateArray())
            {
                if (alias.ValueKind == JsonValueKind.String && fineAliases.Count < 3)
                {
                    fineAliases.Add(alias.GetString()!);
                }
            }
        }

        // DMX ranges are written at the channel's full resolution (0-65535 for a 16-bit channel)
        // unless dmxValueResolution says otherwise.
        int resolutionBytes = 1 + fineAliases.Count;
        resolutionBytes = ReadString(channel, "dmxValueResolution", 10) switch
        {
            "8bit" => 1,
            "16bit" => 2,
            "24bit" => 3,
            _ => resolutionBytes,
        };

        List<JsonElement> capabilities = [];
        if (channel.TryGetProperty("capability", out JsonElement single) && single.ValueKind == JsonValueKind.Object)
        {
            capabilities.Add(single);
        }
        else if (channel.TryGetProperty("capabilities", out JsonElement many) && many.ValueKind == JsonValueKind.Array)
        {
            if (many.GetArrayLength() > MaxCapabilitiesPerChannel)
            {
                return Result<ChannelInfo>.Fail($"Channel \"{Clean(key)}\" has too many capabilities.");
            }

            capabilities.AddRange(many.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object));
        }

        Dictionary<DmxChannelRole, int> weights = [];
        List<OflParsedColorRange> colorRanges = [];
        Dictionary<string, string> switching = new(StringComparer.Ordinal);
        bool isSingle = capabilities.Count == 1;

        foreach (JsonElement capability in capabilities)
        {
            (int start, int end) = isSingle && !capability.TryGetProperty("dmxRange", out _)
                ? (0, 255)
                : ReadRange(capability, resolutionBytes);

            string? type = ReadString(capability, "type", 40);
            DmxChannelRole role = RoleFor(type, capability, wheels, key);

            weights[role] = weights.GetValueOrDefault(role) + (end - start + 1);

            string? hex = RangeColor(type, capability, wheels, key);
            if (hex != null)
            {
                colorRanges.Add(new OflParsedColorRange((byte)start, (byte)end, hex));
            }

            if (capability.TryGetProperty("switchChannels", out JsonElement switches) && switches.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty alias in switches.EnumerateObject())
                {
                    // The first capability listed is the default (lowest DMX values).
                    if (alias.Value.ValueKind == JsonValueKind.String)
                    {
                        switching.TryAdd(alias.Name, alias.Value.GetString()!);
                    }
                }
            }
        }

        // The role covering the widest DMX range wins; "no function" gaps don't count unless
        // that's all there is.
        DmxChannelRole chosen = weights.Count == 0
            ? DmxChannelRole.Generic
            : weights
                .OrderBy(w => w.Key == DmxChannelRole.NoFunction ? 1 : 0)
                .ThenByDescending(w => w.Value)
                .First().Key;

        // Colour ranges only mean something on channels that pick a colour (or close the shutter).
        if (chosen is not (DmxChannelRole.ColorWheel or DmxChannelRole.ColorMacro or DmxChannelRole.Strobe))
        {
            colorRanges.Clear();
        }

        return Result<ChannelInfo>.Ok(new ChannelInfo(key, chosen, ReadDefaultValue(channel, resolutionBytes), fineAliases, switching, colorRanges));
    }

    private static DmxChannelRole RoleFor(string? type, JsonElement capability, Dictionary<string, JsonElement> wheels, string channelKey) => type switch
    {
        "Intensity" => DmxChannelRole.Intensity,
        "ColorIntensity" => ColorIntensityRoles.GetValueOrDefault(ReadString(capability, "color", 30) ?? string.Empty, DmxChannelRole.Generic),
        "ColorPreset" => DmxChannelRole.ColorMacro,
        "ColorTemperature" => DmxChannelRole.ColorTemperature,
        "WheelSlot" or "WheelRotation" or "WheelShake" or "WheelSlotRotation" => WheelRole(capability, wheels, channelKey),
        "ShutterStrobe" or "StrobeSpeed" or "StrobeDuration" => DmxChannelRole.Strobe,
        "Pan" or "PanContinuous" => DmxChannelRole.Pan,
        "Tilt" or "TiltContinuous" => DmxChannelRole.Tilt,
        "PanTiltSpeed" => DmxChannelRole.PanTiltSpeed,
        "Prism" or "PrismRotation" => DmxChannelRole.Prism,
        "Focus" => DmxChannelRole.Focus,
        "Zoom" or "BeamAngle" => DmxChannelRole.Zoom,
        "Iris" or "IrisEffect" => DmxChannelRole.Iris,
        "Frost" or "FrostEffect" => DmxChannelRole.Frost,
        "Speed" or "EffectSpeed" => DmxChannelRole.Speed,
        "Effect" or "EffectParameter" or "EffectDuration" or "SoundSensitivity" => DmxChannelRole.Effect,
        "Fog" or "FogOutput" or "FogType" => DmxChannelRole.Fog,
        "Maintenance" => DmxChannelRole.Maintenance,
        "NoFunction" => DmxChannelRole.NoFunction,
        _ => DmxChannelRole.Generic,
    };

    private static DmxChannelRole WheelRole(JsonElement capability, Dictionary<string, JsonElement> wheels, string channelKey)
    {
        if (!TryGetWheel(capability, wheels, channelKey, out JsonElement wheel)
            || !wheel.TryGetProperty("slots", out JsonElement slots) || slots.ValueKind != JsonValueKind.Array)
        {
            return DmxChannelRole.Generic;
        }

        // A wheel is whatever most of its slots are.
        Dictionary<string, int> kinds = new(StringComparer.Ordinal);
        foreach (JsonElement slot in slots.EnumerateArray())
        {
            string kind = ReadString(slot, "type", 30) ?? string.Empty;
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
        }

        string dominant = kinds.Where(k => k.Key != "Open").OrderByDescending(k => k.Value).Select(k => k.Key).FirstOrDefault() ?? string.Empty;

        return dominant switch
        {
            "Color" => DmxChannelRole.ColorWheel,
            "Gobo" or "AnimationGoboStart" or "AnimationGoboEnd" => DmxChannelRole.Gobo,
            "Prism" => DmxChannelRole.Prism,
            "Iris" => DmxChannelRole.Iris,
            "Frost" => DmxChannelRole.Frost,
            _ => DmxChannelRole.Generic,
        };
    }

    // The colour a range produces: a colour preset's colour, a colour-wheel slot's colour (open =
    // white), or black for a closed shutter / white for an open one.
    private static string? RangeColor(string? type, JsonElement capability, Dictionary<string, JsonElement> wheels, string channelKey)
    {
        switch (type)
        {
            case "ColorPreset":
                return FirstColor(capability);

            case "WheelSlot":
                if (!TryGetWheel(capability, wheels, channelKey, out JsonElement wheel)
                    || !wheel.TryGetProperty("slots", out JsonElement slots) || slots.ValueKind != JsonValueKind.Array
                    || !capability.TryGetProperty("slotNumber", out JsonElement slotNumber) || slotNumber.ValueKind != JsonValueKind.Number
                    || !slotNumber.TryGetDouble(out double number) || number != Math.Floor(number))
                {
                    // Half-way between slots (a split colour) has no single colour.
                    return null;
                }

                int index = (int)number - 1;
                if (index < 0 || index >= slots.GetArrayLength())
                {
                    return null;
                }

                JsonElement slot = slots[index];
                string? slotType = ReadString(slot, "type", 30);

                return slotType switch
                {
                    "Open" => "#FFFFFF",
                    "Color" => FirstColor(slot),
                    _ => null,
                };

            case "ShutterStrobe":
                return ReadString(capability, "shutterEffect", 30) switch
                {
                    "Closed" => "#000000",
                    "Open" => "#FFFFFF",
                    _ => null,
                };

            default:
                return null;
        }
    }

    private static string? FirstColor(JsonElement element)
    {
        if (!element.TryGetProperty("colors", out JsonElement colors) || colors.ValueKind != JsonValueKind.Array || colors.GetArrayLength() == 0)
        {
            return null;
        }

        // Several colours means a split/multi-colour slot; average them for a swatch.
        List<(int R, int G, int B)> parsed = [];
        foreach (JsonElement color in colors.EnumerateArray().Take(8))
        {
            string? hex = color.ValueKind == JsonValueKind.String ? color.GetString() : null;
            if (hex != null && HexColor().IsMatch(hex))
            {
                int rgb = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                parsed.Add(((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF));
            }
        }

        if (parsed.Count == 0)
        {
            return null;
        }

        int r = (int)parsed.Average(c => c.R);
        int g = (int)parsed.Average(c => c.G);
        int b = (int)parsed.Average(c => c.B);

        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static bool TryGetWheel(JsonElement capability, Dictionary<string, JsonElement> wheels, string channelKey, out JsonElement wheel)
    {
        // "wheel" names it explicitly; otherwise the wheel has the channel's own name.
        string wheelName = ReadString(capability, "wheel", 200) ?? channelKey;

        return wheels.TryGetValue(wheelName, out wheel) && wheel.ValueKind == JsonValueKind.Object;
    }

    private static (int Start, int End) ReadRange(JsonElement capability, int resolutionBytes)
    {
        if (!capability.TryGetProperty("dmxRange", out JsonElement range) || range.ValueKind != JsonValueKind.Array || range.GetArrayLength() != 2
            || !range[0].TryGetInt64(out long start) || !range[1].TryGetInt64(out long end))
        {
            return (0, 255);
        }

        long divisor = resolutionBytes switch { 2 => 256, 3 => 65536, _ => 1 };

        int start8 = (int)Math.Clamp(start / divisor, 0, 255);
        int end8 = (int)Math.Clamp(end / divisor, 0, 255);

        return start8 <= end8 ? (start8, end8) : (end8, start8);
    }

    // A number at the channel's resolution, or a percentage string such as "50%".
    private static byte ReadDefaultValue(JsonElement channel, int resolutionBytes)
    {
        if (!channel.TryGetProperty("defaultValue", out JsonElement value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
        {
            long divisor = resolutionBytes switch { 2 => 256, 3 => 65536, _ => 1 };
            return (byte)Math.Clamp(number / divisor, 0, 255);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString() ?? string.Empty;
            if (text.EndsWith('%') && double.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
            {
                return (byte)Math.Clamp(Math.Round(percent / 100 * 255), 0, 255);
            }
        }

        return 0;
    }

    private static Dictionary<string, JsonElement> ReadObject(JsonElement parent, string property)
    {
        Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);

        if (parent.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty item in value.EnumerateObject().Take(MaxAvailableChannels))
            {
                result[item.Name] = item.Value;
            }
        }

        return result;
    }

    private static string? ReadString(JsonElement parent, string property, int maxLength = MaxNameLength) =>
        parent.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? Clean(value.GetString(), maxLength)
            : null;

    private static string? Clean(string? value, int maxLength = MaxNameLength)
    {
        if (value == null)
        {
            return null;
        }

        string trimmed = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();

        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    private sealed record ChannelInfo(
        string Key,
        DmxChannelRole Role,
        byte DefaultValue,
        IReadOnlyList<string> FineAliases,
        IReadOnlyDictionary<string, string> SwitchingAliases,
        IReadOnlyList<OflParsedColorRange> ColorRanges);

    private sealed record AliasInfo(string ChannelKey, byte FineLevel);

    private sealed record TemplateInfo(Regex Pattern, string Key, ChannelInfo Channel, IReadOnlyList<(Regex Pattern, byte FineLevel)> FinePatterns);
}

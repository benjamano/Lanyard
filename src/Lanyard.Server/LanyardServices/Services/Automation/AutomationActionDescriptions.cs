#nullable enable

using System.Text.Json;

namespace Lanyard.Application.Services;

/// <param name="Text">What the action does, in plain words, such as "Start lighting scene 'Party' on Arena 1".</param>
public sealed record AutomationActionDescription(Guid ActionId, string ActionType, string Text);

/// <summary>
/// Turns an action's type and parameters into a sentence for people, for the test-run confirm and
/// result dialogs. Pure: the names come from the caller's lookup, so it can be tested on its own.
/// </summary>
public static class AutomationActionDescriptions
{
    /// <param name="nameOf">The name of a kiosk or a referenced row, or null when it can't be found.</param>
    public static string Describe(
        string actionType,
        string parametersJson,
        Func<Guid, string?> kioskName,
        Func<AutomationActionTargetKind, Guid, string?> nameOf)
    {
        string? operation = null;
        int? holdMilliseconds = null;
        bool resumeIfLoaded = false;
        string? shuffle = null;
        string? repeat = null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(parametersJson);

            if (doc.RootElement.TryGetProperty("Operation", out JsonElement op) && op.ValueKind == JsonValueKind.String)
            {
                operation = op.GetString();
            }

            if (doc.RootElement.TryGetProperty("HoldMilliseconds", out JsonElement hold) && hold.ValueKind == JsonValueKind.Number)
            {
                holdMilliseconds = hold.GetInt32();
            }

            if (doc.RootElement.TryGetProperty("ResumeIfLoaded", out JsonElement resume) && resume.ValueKind == JsonValueKind.True)
            {
                resumeIfLoaded = true;
            }

            if (doc.RootElement.TryGetProperty("Shuffle", out JsonElement shuffleElement) && shuffleElement.ValueKind == JsonValueKind.String)
            {
                shuffle = shuffleElement.GetString();
            }

            if (doc.RootElement.TryGetProperty("Repeat", out JsonElement repeatElement) && repeatElement.ValueKind == JsonValueKind.String)
            {
                repeat = repeatElement.GetString();
            }
        }
        catch (JsonException)
        {
            return "An action whose settings couldn't be read";
        }

        if (!AutomationActionTargets.TryRead(actionType, parametersJson, out AutomationActionTarget target))
        {
            return "An action whose settings couldn't be read";
        }

        string Named(AutomationActionTargetKind kind, string fallback)
        {
            Guid id = target.References.FirstOrDefault(r => r.Kind == kind).Id;

            return id == Guid.Empty ? fallback : $"'{nameOf(kind, id) ?? "a deleted " + AutomationActionTargets.Describe(kind)}'";
        }

        string what = actionType switch
        {
            AutomationActionTypes.MusicControl => operation == "Pause"
                ? "Pause the music"
                : target.References.Any(r => r.Kind == AutomationActionTargetKind.Playlist)
                    ? $"Play playlist {Named(AutomationActionTargetKind.Playlist, "")}"
                    : "Play the music",

            AutomationActionTypes.StartProjectionProgram =>
                $"Start projection program {Named(AutomationActionTargetKind.ProjectionProgram, "(none chosen)")}",

            AutomationActionTypes.StopProjectionProgram => "Stop the projection program",

            AutomationActionTypes.ProjectionProgramControl => (operation ?? ProjectionProgramControlActionExecutor.Start) switch
            {
                ProjectionProgramControlActionExecutor.Start => $"Start projection program {Named(AutomationActionTargetKind.ProjectionProgram, "(none chosen)")}",
                ProjectionProgramControlActionExecutor.Stop => "Stop the projection program",
                ProjectionProgramControlActionExecutor.Pause => "Pause the projection program",
                ProjectionProgramControlActionExecutor.Resume => "Resume the projection program",
                ProjectionProgramControlActionExecutor.SkipToNext => "Skip to the next projection step",
                ProjectionProgramControlActionExecutor.SkipToPrevious => "Go back a projection step",
                _ => $"Projection: {operation}"
            },

            AutomationActionTypes.DmxSceneControl => (operation ?? DmxSceneControlActionExecutor.StartScene) switch
            {
                DmxSceneControlActionExecutor.StartScene => $"Start lighting scene {Named(AutomationActionTargetKind.DmxScene, "(none chosen)")}"
                    + (holdMilliseconds is > 0 ? $" for {holdMilliseconds.Value / 1000.0:0.#} seconds" : ""),
                DmxSceneControlActionExecutor.StopScene => $"Stop lighting scene {Named(AutomationActionTargetKind.DmxScene, "(none chosen)")}",
                DmxSceneControlActionExecutor.StopAllScenes => "Stop all lighting scenes",
                _ => $"Lighting: {operation}"
            },

            AutomationActionTypes.PlayAudioClip => $"Play audio clip {Named(AutomationActionTargetKind.AudioClip, "(none chosen)")}",

            _ => actionType
        };

        string extras = actionType == AutomationActionTypes.MusicControl && operation != "Pause"
            ? MusicExtras(resumeIfLoaded && target.References.Any(r => r.Kind == AutomationActionTargetKind.Playlist), shuffle, repeat)
            : "";

        if (target.ClientId == Guid.Empty)
        {
            return AutomationActionTargets.IsKnown(actionType) ? $"{what} (no kiosk chosen){extras}" : what;
        }

        return $"{what} on {kioskName(target.ClientId) ?? "a deleted kiosk"}{extras}";
    }

    // Music Play's optional settings, such as ", carrying on if it's already loaded, shuffle on".
    private static string MusicExtras(bool resumeIfLoaded, string? shuffle, string? repeat)
    {
        List<string> parts = [];

        if (resumeIfLoaded)
        {
            parts.Add("carrying on if it's already loaded");
        }

        if (shuffle is MusicControlActionExecutor.On or MusicControlActionExecutor.Off)
        {
            parts.Add($"shuffle {shuffle.ToLowerInvariant()}");
        }

        string? repeatText = repeat switch
        {
            "Off" => "repeat off",
            "All" => "repeat the playlist",
            "One" => "repeat the song",
            _ => null
        };

        if (repeatText is not null)
        {
            parts.Add(repeatText);
        }

        return parts.Count == 0 ? "" : ", " + string.Join(", ", parts);
    }
}

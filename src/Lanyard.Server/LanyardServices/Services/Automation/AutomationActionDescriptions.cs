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

        if (target.ClientId == Guid.Empty)
        {
            return AutomationActionTargets.IsKnown(actionType) ? $"{what} (no kiosk chosen)" : what;
        }

        return $"{what} on {kioskName(target.ClientId) ?? "a deleted kiosk"}";
    }
}

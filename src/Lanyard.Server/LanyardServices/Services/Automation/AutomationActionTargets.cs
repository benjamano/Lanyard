#nullable enable

using System.Text.Json;

namespace Lanyard.Application.Services;

/// <summary>What kind of company-owned row an action's parameters point at, besides its kiosk.</summary>
public enum AutomationActionTargetKind
{
    Playlist,
    ProjectionProgram,
    DmxScene,
    AudioClip
}

/// <param name="ClientId">The kiosk the action drives; Guid.Empty when it hasn't been chosen yet.</param>
/// <param name="References">Other rows the action names (a playlist, a program, a scene or a clip).</param>
public sealed record AutomationActionTarget(Guid ClientId, IReadOnlyList<(AutomationActionTargetKind Kind, Guid Id)> References);

/// <summary>
/// Reads the kiosk and the other rows an action's ParametersJson points at, for every action type,
/// so the company each one belongs to can be checked against the rule's.
/// </summary>
/// <remarks>
/// ParametersJson is just text that the executors trust, and they look kiosks up through the
/// unfiltered system context. Without this, only DMX actions were checked, and a music,
/// projection or audio clip action could name another company's kiosk.
///
/// Every executor reads the same property names (PascalCase, the default serializer), so one
/// shape covers them all. A new action type must be added to <see cref="IsKnown"/>;
/// AutomationActionDispatchCoverageTests fails until it is.
/// </remarks>
public static class AutomationActionTargets
{
    private sealed record Parameters
    {
        public Guid TargetClientId { get; init; }
        public string? Operation { get; init; }
        public Guid? PlaylistId { get; init; }
        public Guid? ProjectionProgramId { get; init; }
        public Guid? SceneId { get; init; }
        public Guid? AudioClipId { get; init; }
    }

    private static readonly HashSet<string> KnownTypes =
    [
        AutomationActionTypes.MusicControl,
        AutomationActionTypes.StartProjectionProgram,
        AutomationActionTypes.StopProjectionProgram,
        AutomationActionTypes.ProjectionProgramControl,
        AutomationActionTypes.DmxSceneControl,
        AutomationActionTypes.PlayAudioClip
    ];

    public static bool IsKnown(string actionType) => KnownTypes.Contains(actionType);

    /// <summary>
    /// False when the JSON can't be read. An unknown action type reads as no targets: no
    /// executor handles it, so the engine records it as not supported anyway.
    /// </summary>
    public static bool TryRead(string actionType, string parametersJson, out AutomationActionTarget target)
    {
        target = new AutomationActionTarget(Guid.Empty, []);

        if (!IsKnown(actionType))
        {
            return true;
        }

        Parameters? parameters;

        try
        {
            parameters = JsonSerializer.Deserialize<Parameters>(parametersJson);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parameters == null)
        {
            return false;
        }

        List<(AutomationActionTargetKind, Guid)> references = [];

        void Add(AutomationActionTargetKind kind, Guid? id)
        {
            if (id is Guid value && value != Guid.Empty)
            {
                references.Add((kind, value));
            }
        }

        switch (actionType)
        {
            case AutomationActionTypes.MusicControl:
                Add(AutomationActionTargetKind.Playlist, parameters.PlaylistId);
                break;

            case AutomationActionTypes.StartProjectionProgram:
                Add(AutomationActionTargetKind.ProjectionProgram, parameters.ProjectionProgramId);
                break;

            case AutomationActionTypes.ProjectionProgramControl:
                // Only Start plays a program; the other operations act on whatever is running.
                if ((parameters.Operation ?? ProjectionProgramControlActionExecutor.Start) == ProjectionProgramControlActionExecutor.Start)
                {
                    Add(AutomationActionTargetKind.ProjectionProgram, parameters.ProjectionProgramId);
                }
                break;

            case AutomationActionTypes.DmxSceneControl:
                // StopAllScenes targets the whole kiosk, so it is the only operation without a scene.
                if (parameters.Operation != DmxSceneControlActionExecutor.StopAllScenes)
                {
                    Add(AutomationActionTargetKind.DmxScene, parameters.SceneId);
                }
                break;

            case AutomationActionTypes.PlayAudioClip:
                Add(AutomationActionTargetKind.AudioClip, parameters.AudioClipId);
                break;
        }

        target = new AutomationActionTarget(parameters.TargetClientId, references);

        return true;
    }

    /// <summary>"music", "projection", "DMX" or "audio clip", for messages about an action.</summary>
    public static string Describe(string actionType) => actionType switch
    {
        AutomationActionTypes.MusicControl => "music",
        AutomationActionTypes.StartProjectionProgram or AutomationActionTypes.StopProjectionProgram or AutomationActionTypes.ProjectionProgramControl => "projection",
        AutomationActionTypes.DmxSceneControl => "DMX",
        AutomationActionTypes.PlayAudioClip => "audio clip",
        _ => actionType
    };

    public static string Describe(AutomationActionTargetKind kind) => kind switch
    {
        AutomationActionTargetKind.Playlist => "playlist",
        AutomationActionTargetKind.ProjectionProgram => "projection program",
        AutomationActionTargetKind.DmxScene => "scene",
        AutomationActionTargetKind.AudioClip => "audio clip",
        _ => kind.ToString()
    };
}

namespace Lanyard.Infrastructure.Enum;

public enum ButtonActionType
{
    Unknown = 0,
    TriggerProjectionProgram = 1,
    PauseProjectionProgram = 2,
    ResumeProjectionProgram = 3,
    SkipProjectionProgramStep = 4,
    StopProjectionProgram = 5
}

public static class ButtonActionTypeExtensions
{
    // Single source of truth for "does this action control a projection program" - both
    // RenderButtonWidget.razor's security gate and ConfigureButtonDialog.razor's field
    // visibility read this same set, so a new action type can't update one and silently
    // miss the other.
    private static readonly HashSet<ButtonActionType> ProjectionControlActionTypes =
    [
        ButtonActionType.TriggerProjectionProgram,
        ButtonActionType.PauseProjectionProgram,
        ButtonActionType.ResumeProjectionProgram,
        ButtonActionType.SkipProjectionProgramStep,
        ButtonActionType.StopProjectionProgram
    ];

    public static bool IsProjectionControlAction(this ButtonActionType actionType) =>
        ProjectionControlActionTypes.Contains(actionType);
}

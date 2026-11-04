#nullable enable

using Lanyard.Shared.Enum;

namespace Lanyard.Application.Services;

/// <param name="FinalSnapshot">
/// Only on the InGame -> NotStarted edge (a game ending), and only when the hub passed the
/// publish: the finished game's scores, for GameEnded rules. Null on every other edge.
/// </param>
public sealed record GameStatusTransitionEvent(
    Guid ClientId,
    GameStatus PreviousStatus,
    GameStatus NewStatus,
    GameSnapshot? FinalSnapshot = null);

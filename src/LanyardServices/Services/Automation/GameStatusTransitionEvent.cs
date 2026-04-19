#nullable enable

using Lanyard.Shared.Enum;

namespace Lanyard.Application.Services;

public sealed record GameStatusTransitionEvent(
    Guid ClientId,
    GameStatus PreviousStatus,
    GameStatus NewStatus);

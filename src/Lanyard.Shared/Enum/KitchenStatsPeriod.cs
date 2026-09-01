namespace Lanyard.Shared.Enum;

/// <summary>
/// Window a kitchen-stats reading covers.
///
/// In Lanyard.Shared rather than Infrastructure because a custom kitchen client selects the
/// window when it calls the API, and should not need the server's EF model to name one.
/// </summary>
public enum KitchenStatsPeriod
{
    /// <summary>Since midnight local to the venue. The default: a kitchen cares about this service.</summary>
    Today = 0,

    /// <summary>Rolling last hour - closer to "how are we doing right now" during a rush.</summary>
    LastHour = 1,

    ThisWeek = 2
}

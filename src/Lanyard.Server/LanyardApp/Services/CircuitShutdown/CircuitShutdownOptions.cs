namespace Lanyard.App.Services.CircuitShutdown;

public sealed class CircuitShutdownOptions
{
    public const string SectionName = "CircuitShutdown";

    // How long shutdown waits for open browser sessions to finish pausing before the server
    // stops anyway. Must stay below the host's ShutdownTimeout (30s by default) and below the
    // platform's SIGTERM-to-SIGKILL gap (Railway: RAILWAY_DEPLOYMENT_DRAINING_SECONDS).
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

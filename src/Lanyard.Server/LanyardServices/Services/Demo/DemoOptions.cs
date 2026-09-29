namespace Lanyard.Application.Services.Demo;

// "Demo" section of configuration (Demo__Enabled etc. in the environment).
public class DemoOptions
{
    public const string SectionName = "Demo";

    // Turns on everything demo-related: seeding the demo company, the homepage's one-click logins
    // and the nightly reset. Off by default, so nothing about the demo exists until it's set.
    public bool Enabled { get; set; }

    // When the demo company is wiped and reseeded each day, in UK time.
    public TimeOnly ResetTimeUk { get; set; } = new(4, 0);
}

namespace Lanyard.Shared.Enum;

/// <summary>The result of one kiosk self-test check. Stored as an int, so never renumber.</summary>
public enum SelfTestOutcome
{
    Pass = 1,

    // Working, but something is worth a look (e.g. no game traffic, which is normal between games).
    Warn = 2,

    Fail = 3,

    // Deliberately not run, e.g. the test tone while music is playing.
    Skipped = 4,

    // This kiosk isn't set up to use the hardware, so there is nothing to test.
    NotConfigured = 5
}

/// <summary>What happened to the test tone, which the kiosk only plays when nothing else is.</summary>
public enum SelfTestToneResult
{
    Played = 1,
    Failed = 2,
    SkippedMusicPlaying = 3,
    SkippedClipPlaying = 4,
    SkippedNoOutput = 5
}

/// <summary>What happened to the optional DMX flash.</summary>
public enum SelfTestFlashResult
{
    NotRequested = 0,
    Flashed = 1,
    Failed = 2,
    SkippedOutputLive = 3,
    SkippedPortNotOpen = 4
}

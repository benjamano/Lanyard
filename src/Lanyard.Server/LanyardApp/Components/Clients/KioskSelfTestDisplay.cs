using Lanyard.Application.Services;
using Lanyard.Shared.Enum;
using Lanyard.Shared.Kiosk;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Lanyard.App.Components.Clients;

/// <summary>Wording and colours for kiosk self-test results, kept out of the markup so it can be tested.</summary>
public static class KioskSelfTestDisplay
{
    public static string OutcomeLabel(SelfTestOutcome outcome) => outcome switch
    {
        SelfTestOutcome.Pass => "Passed",
        SelfTestOutcome.Warn => "Check",
        SelfTestOutcome.Fail => "Failed",
        SelfTestOutcome.Skipped => "Skipped",
        SelfTestOutcome.NotConfigured => "Not set up",
        _ => "Unknown"
    };

    /// <summary>A CSS modifier for the row, e.g. "self-test-check-fail".</summary>
    public static string OutcomeClass(SelfTestOutcome outcome) => outcome switch
    {
        SelfTestOutcome.Pass => "self-test-check-pass",
        SelfTestOutcome.Warn => "self-test-check-warn",
        SelfTestOutcome.Fail => "self-test-check-fail",
        _ => "self-test-check-neutral"
    };

    /// <summary>
    /// One line for the top of the results, e.g. "2 problems and 1 warning". Null when the kiosk
    /// didn't return a checklist; the run's error says why instead.
    /// </summary>
    public static string? Summary(KioskSelfTestRunView run)
    {
        if (run.Status != ClientSelfTestStatus.Completed)
        {
            return null;
        }

        int fails = run.Count(SelfTestOutcome.Fail);
        int warns = run.Count(SelfTestOutcome.Warn);

        if (fails == 0 && warns == 0)
        {
            return "Everything passed";
        }

        List<string> parts = [];

        if (fails > 0)
        {
            parts.Add(fails == 1 ? "1 problem" : $"{fails} problems");
        }

        if (warns > 0)
        {
            parts.Add(warns == 1 ? "1 thing to check" : $"{warns} things to check");
        }

        return string.Join(" and ", parts);
    }

    public static MessageBarIntent SummaryIntent(KioskSelfTestRunView run)
    {
        if (run.Status != ClientSelfTestStatus.Completed || run.Count(SelfTestOutcome.Fail) > 0)
        {
            return MessageBarIntent.Error;
        }

        return run.Count(SelfTestOutcome.Warn) > 0 ? MessageBarIntent.Warning : MessageBarIntent.Success;
    }

    /// <summary>A short outcome for the history list, e.g. "Everything passed" or "Kiosk didn't reply".</summary>
    public static string ShortResult(KioskSelfTestRunView run) => run.Status switch
    {
        ClientSelfTestStatus.Completed => Summary(run)!,
        ClientSelfTestStatus.TimedOut => "Kiosk didn't reply",
        ClientSelfTestStatus.KioskTooOld => "Kiosk needs updating",
        _ => "Couldn't run"
    };

    /// <summary>"Ada Min", or a placeholder once the person's account has been erased.</summary>
    public static string RequestedBy(KioskSelfTestRunView run)
    {
        return string.IsNullOrWhiteSpace(run.RequestedByName) ? "a former member of staff" : run.RequestedByName;
    }

    /// <summary>
    /// What to tell someone standing by the kiosk to listen or look for, or null. The kiosk can't
    /// hear its own speakers, so "it played" only means Windows accepted the sound.
    /// </summary>
    public static string? AskSomeoneNearby(KioskSelfTestCheckView check) => (check.Key, check.Outcome) switch
    {
        (SelfTestCheckKeys.AudioTone, SelfTestOutcome.Pass) => "Did someone by the kiosk hear a short beep? If not, check the speakers or amplifier are on and not muted.",
        (SelfTestCheckKeys.DmxFlash, SelfTestOutcome.Pass) => "Did the lights flash? If not, check the DMX cable from the interface to the first light, and each light's DMX address.",
        _ => null
    };

    /// <summary>For the Health tab: "Last self-test 2 days ago: 1 problem".</summary>
    public static string HealthLine(KioskSelfTestRunView? latest, DateTime utcNow)
    {
        if (latest == null)
        {
            return "No self-test has been run on this kiosk.";
        }

        return $"Last self-test {KioskHealthDisplay.Ago(utcNow - latest.RequestedAtUtc)}: {char.ToLowerInvariant(ShortResult(latest)[0])}{ShortResult(latest)[1..]}.";
    }
}

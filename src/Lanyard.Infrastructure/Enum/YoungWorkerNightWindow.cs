namespace Lanyard.Infrastructure.Enum;

// The night hours staff under 18 can't work (UK Working Time Regulations). The default is 22:00 to
// 06:00; an employment contract can move it to 23:00 to 07:00. Midnight to 04:00 is never allowed
// whichever is chosen. Stored as its int value, so never renumber an existing member.
public enum YoungWorkerNightWindow
{
    TenToSix = 0,
    ElevenToSeven = 1
}

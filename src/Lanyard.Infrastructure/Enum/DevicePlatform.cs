namespace Lanyard.Infrastructure.Enum;

// The broad kind of device a browser is on. Decides which install steps to show and labels a
// device in the person's list; not used for anything security-relevant.
public enum DevicePlatform
{
    Other = 0,
    IPhone = 1,
    IPad = 2,
    Android = 3,
    Desktop = 4
}

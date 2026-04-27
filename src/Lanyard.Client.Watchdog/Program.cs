using System.Diagnostics;

const string ClientExeName = "Lanyard.Client.exe";
const int RestartDelayMs = 3000;
const int MaxConsecutiveCrashed = 5;
const int CrashWindowSeconds = 30;

// Hide the console window as we dont care about it
nint handle = GetConsoleWindow();
ShowWindow(handle, 0);

string clientPath = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory,
    ClientExeName
));

int consecutiveCrashes = 0;

while (true)
{
    Console.WriteLine($"Starting Lanyard Client at {DateTime.UtcNow}");

    // Using UTC as I hate daylight savings time

    DateTime startTime = DateTime.UtcNow;

    Process clientProcess = Process.Start(new ProcessStartInfo
    {
        FileName = clientPath,
        UseShellExecute = true
    })!;

    if (clientProcess == null)
    {
        await Task.Delay(RestartDelayMs);
        continue;
    }

    await clientProcess.WaitForExitAsync();

    // If it ran for a while we will reset the crash counter as it's stable enough
    if ((DateTime.UtcNow - startTime).TotalSeconds > CrashWindowSeconds)
    {
        Console.WriteLine("Lanyard Client ran for a while, resetting crash counter.");

        consecutiveCrashes = 0;
    }
    else
    {
        Console.WriteLine("Lanyard Client crashed quickly.");

        consecutiveCrashes++;
    }

    // Too many crashes in a small period, something is bad, give up for now
    // TODO: Setup an email system to email us when this happens so I can fix
    if (consecutiveCrashes >= MaxConsecutiveCrashed)
    {
        Console.WriteLine("Lanyard Client has crashed too many times rapidly. Watchdog giving up.");

        LogCrash("Lanyard Client crashed too many times rapidly. Watchdog giving up.");
        return;
    }

    await Task.Delay(RestartDelayMs);
}

static void LogCrash(string message)
{
    try
    {
        string logPath = Path.Combine(AppContext.BaseDirectory, "watchdog_crash_log.txt");
        string logMessage = $"{DateTime.UtcNow}: {message}{Environment.NewLine}";
        File.AppendAllText(logPath, logMessage);
    }
    catch
    {
        // If logging fails, there's not much we can do, just swallow the exception and cry 
    }
}

[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern IntPtr GetConsoleWindow();

[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
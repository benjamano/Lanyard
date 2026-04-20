using Microsoft.Win32.TaskScheduler;

public static class StartupScheduler
{
    private const string TaskName = "LanyardClientStartup";

    public static void EnsureStartupTaskExists()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return;
        }

        if (Environment.GetEnvironmentVariable("LANYARD_CLIENT_SKIP_ADDING_WATCHDOG_STARTUP_TASK") == "true" || Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development")
        {
            // DELETE THE STARTUP TASK TOO

            DeleteStartupTask();

            return;
        }

        using TaskService ts = new();

        if (ts.GetTask(TaskName) != null)
        {
            return;
        }

        Console.WriteLine("Start task not found. Creating startup task...");

        TaskDefinition td = ts.NewTask();

        td.RegistrationInfo.Description = "Starts the Lanyard Client on user logon. This task was automatically created by the Lanyard Client. You can safely delete this task if you do not want the Lanyard Client to start on logon.";
        
        td.Triggers.Add(new LogonTrigger { UserId = Environment.UserName });
        
        string watchdogPath = Path.Combine(AppContext.BaseDirectory, "Lanyard.Client.Watchdog.exe");

        td.Actions.Add(new ExecAction(
            watchdogPath,
            null,
            AppContext.BaseDirectory
        ));

        td.Principal.RunLevel = TaskRunLevel.LUA;

        td.Settings.DisallowStartIfOnBatteries = false;

        td.Settings.StopIfGoingOnBatteries = false;

        ts.RootFolder.RegisterTaskDefinition(TaskName, td);
    }

    private static void DeleteStartupTask()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return;
        }

        using TaskService ts = new();

        if (ts.GetTask(TaskName) == null)
        {
            return;
        }

        Console.WriteLine("Deleting startup task...");

        ts.RootFolder.DeleteTask(TaskName);
    }
}
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.Enum;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.TaskScheduler;

namespace Lanyard.Client.RestartScheduler;

public class RestartSchedulerService(ILogger<RestartSchedulerService> logger) : IRestartSchedulerService
{
    private const string TaskName = "LanyardClientAutoRestart";

    private readonly ILogger<RestartSchedulerService> _logger = logger;

    public void Apply(ClientRestartScheduleDTO schedule)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            _logger.LogInformation("Skipping restart schedule; not running on Windows");
            return;
        }

        try
        {
            using TaskService ts = new();

            if (!schedule.Enabled)
            {
                if (ts.GetTask(TaskName) != null)
                {
                    _logger.LogInformation("Auto restart disabled; removing scheduled task {TaskName}", TaskName);
                    ts.RootFolder.DeleteTask(TaskName);
                }

                return;
            }

            int count = Math.Max(1, schedule.IntervalCount);
            DateTime startBoundary = DateTime.Today.Add(schedule.TimeOfDay.ToTimeSpan());

            TaskDefinition td = ts.NewTask();

            td.RegistrationInfo.Description = "Restarts this PC on a schedule configured from Lanyard Server. This task was automatically created by the Lanyard Client. You can safely delete it to stop scheduled restarts.";

            td.Triggers.Add(BuildTrigger(schedule.IntervalUnit, count, startBoundary));

            // Standard users may restart their own machine, so no elevation is required.
            td.Actions.Add(new ExecAction("shutdown.exe", "/r /t 60 /c \"Lanyard scheduled restart\"", null));

            td.Principal.RunLevel = TaskRunLevel.LUA;

            td.Settings.DisallowStartIfOnBatteries = false;
            td.Settings.StopIfGoingOnBatteries = false;
            td.Settings.StartWhenAvailable = true;

            ts.RootFolder.RegisterTaskDefinition(TaskName, td);

            _logger.LogInformation("Registered auto restart task {TaskName}: every {Count} {Unit} at {Time}", TaskName, count, schedule.IntervalUnit, schedule.TimeOfDay);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply restart schedule");
        }
    }

    private static Trigger BuildTrigger(RestartIntervalUnit unit, int count, DateTime startBoundary)
    {
        switch (unit)
        {
            case RestartIntervalUnit.Week:
                return new WeeklyTrigger
                {
                    StartBoundary = startBoundary,
                    WeeksInterval = (short)count,
                    DaysOfWeek = (DaysOfTheWeek)(1 << (int)startBoundary.DayOfWeek)
                };

            case RestartIntervalUnit.Month:
                // Windows monthly triggers have no native "every N months" interval,
                // so IntervalCount is ignored here: this runs on the anchor day of every month.
                return new MonthlyTrigger
                {
                    StartBoundary = startBoundary,
                    DaysOfMonth = [startBoundary.Day],
                    MonthsOfYear = MonthsOfTheYear.AllMonths
                };

            case RestartIntervalUnit.Day:
            default:
                return new DailyTrigger
                {
                    StartBoundary = startBoundary,
                    DaysInterval = (short)count
                };
        }
    }
}

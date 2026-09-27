using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;

namespace Lanyard.Client.AudioDevices;

/// <summary>
/// Keeps the server-configured output device as the Windows default. Displays with speakers
/// (TVs, projectors over HDMI) register a new audio endpoint every time they wake or reconnect,
/// and Windows often makes that the default, pulling music off the real speakers. Rather than
/// trusting device-change notifications, the preferred device is re-asserted once a minute, which
/// also recovers from anything else that changed the default. The check keeps running while the
/// server is unreachable, using the last settings received.
/// </summary>
public class AudioDeviceService(ILogger<AudioDeviceService> logger) : IAudioDeviceService, IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly ILogger<AudioDeviceService> _logger = logger;
    private readonly object _lock = new();

    private ClientAudioSettingsDTO? _settings;
    private System.Threading.Timer? _timer;

    // Last problem logged, so a missing device warns once rather than every minute.
    private string? _lastReportedProblem;

    public void Apply(ClientAudioSettingsDTO settings)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogInformation("Skipping audio device settings; not running on Windows");
            return;
        }

        lock (_lock)
        {
            _settings = settings;
            _lastReportedProblem = null;
        }

        if (string.IsNullOrWhiteSpace(settings.PreferredDeviceId))
        {
            _logger.LogInformation("No preferred audio device configured; leaving the Windows default alone");
            return;
        }

        // Due immediately, so a new setting (or a reconnect) takes effect without waiting a minute.
        if (_timer == null)
        {
            _timer = new System.Threading.Timer(_ => EnforceDefaultDevice(), null, TimeSpan.Zero, CheckInterval);
        }
        else
        {
            _timer.Change(TimeSpan.Zero, CheckInterval);
        }
    }

    private void EnforceDefaultDevice()
    {
        // The timer can fire while a previous check is still stuck in a slow COM call; skip rather than pile up.
        if (!Monitor.TryEnter(_lock))
        {
            return;
        }

        try
        {
            ClientAudioSettingsDTO? settings = _settings;

            if (settings == null || string.IsNullOrWhiteSpace(settings.PreferredDeviceId))
            {
                return;
            }

            using MMDeviceEnumerator enumerator = new();

            List<MMDevice> devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

            try
            {
                // Match on endpoint ID first; fall back to the name in case the device was plugged
                // into a different port and Windows gave it a fresh endpoint ID. The fallback only
                // applies when the name is unambiguous: two identical TVs share a friendly name, and
                // while the preferred one sleeps its twin must not be forced to default instead.
                MMDevice? target = devices.FirstOrDefault(d => string.Equals(d.ID, settings.PreferredDeviceId, StringComparison.OrdinalIgnoreCase));

                if (target == null && !string.IsNullOrWhiteSpace(settings.PreferredDeviceName))
                {
                    List<MMDevice> nameMatches = devices
                        .Where(d => string.Equals(d.FriendlyName, settings.PreferredDeviceName, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    target = nameMatches.Count == 1 ? nameMatches[0] : null;
                }

                if (target == null)
                {
                    ReportProblemOnce("Preferred audio device {DeviceName} ({DeviceId}) is not connected; leaving the current default in place until it returns",
                        settings.PreferredDeviceName, settings.PreferredDeviceId);
                    return;
                }

                string? currentDefaultId = null;
                string? currentDefaultName = null;

                if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    using MMDevice currentDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    currentDefaultId = currentDefault.ID;
                    currentDefaultName = currentDefault.FriendlyName;
                }

                if (string.Equals(currentDefaultId, target.ID, StringComparison.OrdinalIgnoreCase))
                {
                    _lastReportedProblem = null;
                    return;
                }

                SetDefaultEndpoint(target.ID);

                _lastReportedProblem = null;
                _logger.LogInformation("Default audio device was {CurrentDevice}; switched it back to {TargetDevice}",
                    currentDefaultName ?? "(none)", target.FriendlyName);
            }
            finally
            {
                foreach (MMDevice device in devices)
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            ReportProblemOnce("Failed to enforce the default audio device: {Error}", ex.Message);
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    private static void SetDefaultEndpoint(string deviceId)
    {
        IPolicyConfig policyConfig = (IPolicyConfig)new PolicyConfigClient();

        try
        {
            // Set every role so music, system sounds and anything using the communications device all follow.
            foreach (Role role in new[] { Role.Console, Role.Multimedia, Role.Communications })
            {
                Marshal.ThrowExceptionForHR(policyConfig.SetDefaultEndpoint(deviceId, (int)role));
            }
        }
        finally
        {
            Marshal.ReleaseComObject(policyConfig);
        }
    }

    private void ReportProblemOnce(string messageTemplate, params object?[] args)
    {
        string problem = messageTemplate + string.Join("|", args);

        if (problem == _lastReportedProblem)
        {
            return;
        }

        _lastReportedProblem = problem;
        _logger.LogWarning(messageTemplate, args);
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}

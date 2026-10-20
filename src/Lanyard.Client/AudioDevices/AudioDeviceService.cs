using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.Kiosk;
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

    private volatile ClientAudioSettingsDTO? _settings;
    private System.Threading.Timer? _timer;

    // Last problem logged, so a missing device warns once rather than every minute.
    private string? _lastReportedProblem;

    // Boxed so reads from the heartbeat thread see a whole value; written only under _lock.
    private volatile object? _preferredDevicePresent;

    public bool? PreferredDevicePresent => (bool?)_preferredDevicePresent;

    public string? PreferredDeviceId => _settings?.PreferredDeviceId;

    public string? PreferredDeviceName => _settings?.PreferredDeviceName;

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
            _preferredDevicePresent = null;
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
                _preferredDevicePresent = null;
                return;
            }

            using MMDeviceEnumerator enumerator = new();

            List<MMDevice> devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

            try
            {
                // The same rule the self-test uses: ID first, then an unambiguous name, so a device
                // moved to another port is still found but a sleeping TV's identical twin is not.
                List<SelfTestRules.AudioEndpoint> endpoints = devices.Select(d => new SelfTestRules.AudioEndpoint(d.ID, d.FriendlyName)).ToList();
                SelfTestRules.AudioEndpoint? match = SelfTestRules.MatchPreferredAudio(endpoints, settings.PreferredDeviceId, settings.PreferredDeviceName);
                MMDevice? target = match == null ? null : devices.First(d => d.ID == match.Id);

                _preferredDevicePresent = target != null;

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

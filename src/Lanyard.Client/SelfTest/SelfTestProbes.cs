using Lanyard.Shared.Kiosk;
using Microsoft.Web.WebView2.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SharpPcap;

namespace Lanyard.Client.SelfTest;

// Thin wrappers over the hardware calls the self-test makes. They hold no decisions (those live in
// SelfTestRules in Lanyard.Shared), so the runner reads as a list of facts being gathered.

public interface IAudioEndpointProvider
{
    (List<SelfTestRules.AudioEndpoint> Devices, string? DefaultDeviceId) GetRenderEndpoints();
}

public sealed class AudioEndpointProvider : IAudioEndpointProvider
{
    public (List<SelfTestRules.AudioEndpoint> Devices, string? DefaultDeviceId) GetRenderEndpoints()
    {
        using MMDeviceEnumerator enumerator = new();

        List<SelfTestRules.AudioEndpoint> devices = [];

        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                devices.Add(new SelfTestRules.AudioEndpoint(device.ID, device.FriendlyName));
            }
        }

        string? defaultId = null;

        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            using MMDevice currentDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = currentDefault.ID;
        }

        return (devices, defaultId);
    }
}

public interface ITonePlayer
{
    /// <summary>Plays a short sine tone on the Windows default output; throws if it can't.</summary>
    Task PlayAsync(TimeSpan duration);
}

public sealed class TonePlayer : ITonePlayer
{
    private const int Frequency = 880;

    // Quiet enough not to startle anyone near a speaker, loud enough to hear next to it.
    private const double Gain = 0.15;

    public async Task PlayAsync(TimeSpan duration)
    {
        ISampleProvider tone = new SignalGenerator(44100, 1)
        {
            Type = SignalGeneratorType.Sin,
            Frequency = Frequency,
            Gain = Gain
        }.Take(duration);

        TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Device -1 is the Windows default, which AudioDeviceService keeps on the preferred output.
        using WaveOutEvent output = new();

        output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception != null)
            {
                stopped.TrySetException(e.Exception);
            }
            else
            {
                stopped.TrySetResult();
            }
        };

        output.Init(tone);
        output.Play();

        await stopped.Task.WaitAsync(duration + TimeSpan.FromSeconds(2));
    }
}

public interface ICaptureDeviceCatalog
{
    /// <summary>The network cards Npcap can see. False when Npcap itself is missing.</summary>
    bool TryListDevices(out List<SelfTestRules.CaptureDevice> devices, out string? error);
}

public sealed class CaptureDeviceCatalog : ICaptureDeviceCatalog
{
    public bool TryListDevices(out List<SelfTestRules.CaptureDevice> devices, out string? error)
    {
        devices = [];
        error = null;

        try
        {
            // A fresh list rather than CaptureDeviceList.Instance: that one holds the device the
            // packet sniffer is capturing on, and refreshing it would swap that object out.
            CaptureDeviceList list = CaptureDeviceList.New();

            foreach (ILiveDevice device in list)
            {
                devices.Add(new SelfTestRules.CaptureDevice(device.Name, device.Description, device.MacAddress?.ToString()));
            }

            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            // wpcap.dll comes with Npcap, so its absence means Npcap isn't installed.
            error = ex.Message;
            return false;
        }
    }
}

public interface IWebView2RuntimeProbe
{
    /// <summary>The installed runtime's version, or null with the reason when there isn't one.</summary>
    string? GetRuntimeVersion(out string? error);
}

public sealed class WebView2RuntimeProbe : IWebView2RuntimeProbe
{
    public string? GetRuntimeVersion(out string? error)
    {
        error = null;

        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}

using Lanyard.Infrastructure.DTO.ZoneScoreboard;
using Lanyard.Shared.LaserGame;
using Microsoft.Extensions.Logging;
using PacketDotNet;
using SharpPcap;
using System.Net;
using System.Net.NetworkInformation;

namespace Lanyard.Client.PacketSniffing;

public class PacketSniffer(ILogger<PacketSniffer> logger, IActionFunctions actions) : IPacketSniffer
{
    // Parsed once when settings arrive, so each packet is matched without building strings.
    // Replaced whole rather than mutated, because the capture thread reads them.
    private volatile IPAddress[] _sourceIps = [IPAddress.Parse("192.168.0.10"), IPAddress.Parse("192.168.0.11"), IPAddress.Parse("192.168.2.42")];
    private volatile IPAddress? _destinationIp = IPAddress.Parse("192.168.0.255");

    private readonly ILogger<PacketSniffer> _logger = logger;
    private readonly IActionFunctions _actions = actions;

    // The server resends ReceiveZoneScoreboardSettings on every OnConnectedAsync, so this
    // runs again on every reconnect. Without tracking the active device, OnPacketArrival
    // gets a duplicate += subscription (CaptureDeviceList.Instance returns the same cached
    // device object) and the capture is reopened, so after N reconnects every real packet
    // is processed and republished N times.
    private volatile ILiveDevice? _activeDevice;
    private volatile bool _isConfigured;

    public bool IsConfigured => _isConfigured;
    public bool IsCapturing => _activeDevice != null;

    // Ticks rather than DateTime? so the capture thread can write them without a lock.
    private long _captureStartedTicks;
    private long _lastPacketTicks;
    private long _lastGamePacketTicks;

    public string? PreferredMacAddress { get; private set; }
    public string? ActiveDeviceName => _activeDevice?.Name;
    public DateTime? CaptureStartedUtc => FromTicks(ref _captureStartedTicks);
    public DateTime? LastPacketUtc => FromTicks(ref _lastPacketTicks);
    public DateTime? LastGamePacketUtc => FromTicks(ref _lastGamePacketTicks);

    private static DateTime? FromTicks(ref long ticks)
    {
        long value = Interlocked.Read(ref ticks);

        return value == 0 ? null : new DateTime(value, DateTimeKind.Utc);
    }

    public async Task StartSniffingAsync(ZoneScoreboardSettingsDTO settings)
    {
        _isConfigured = true;
        PreferredMacAddress = settings.PreferredDeviceMacAddress;

        CaptureDeviceList devices = CaptureDeviceList.Instance;

        if (devices.Any() == false)
        {
            _logger.LogWarning("0 usable Packet Sniffing Devices were found!");

            return;
        }

        ApplyAddressFilter(settings);

        PhysicalAddress.TryParse(settings.PreferredDeviceMacAddress, out PhysicalAddress? preferedInterfaceMacAddress);

        ILiveDevice? device = preferedInterfaceMacAddress == null
            ? null
            : devices.FirstOrDefault(x => preferedInterfaceMacAddress.Equals(x.MacAddress));

        if (device == null && devices.Any())
        {
            _logger.LogWarning("Could not find the Prefered Interface with the MAC Address: {PreferedInterfaceMacAddress}, defaulting to first entry.", settings.PreferredDeviceMacAddress);

            device = devices.FirstOrDefault(x=> string.IsNullOrWhiteSpace(x.MacAddress?.ToString()) == false)!;
        }
        else if (device == null)
        {
            _logger.LogError("Could not find any interfaces to sniff!");
            return;
        }

        if (ReferenceEquals(_activeDevice, device))
        {
            _logger.LogInformation("Packet sniffer already running on {DeviceName}; re-applied filter settings without reopening.", device.Name);
            return;
        }

        if (_activeDevice != null)
        {
            _activeDevice.OnPacketArrival -= OnPacketArrival;

            try
            {
                _activeDevice.StopCapture();
                _activeDevice.Close();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing previous packet sniffing device {DeviceName}.", _activeDevice.Name);
            }
        }

        device.OnPacketArrival += OnPacketArrival;

        device.Open(new DeviceConfiguration
        {
            Mode = DeviceModes.Promiscuous,
            ReadTimeout = 1000
        });

        _logger.LogInformation("Started Packet Sniffer on device: {DeviceName} with MAC Address: {MacAddress}", device.Name, device.MacAddress);

        device.StartCapture();

        Interlocked.Exchange(ref _captureStartedTicks, DateTime.UtcNow.Ticks);
        _activeDevice = device;
    }

    private void ApplyAddressFilter(ZoneScoreboardSettingsDTO settings)
    {
        if (IPAddress.TryParse(settings.SourceIp, out IPAddress? sourceIp))
        {
            _sourceIps = [sourceIp];
        }
        else
        {
            _logger.LogWarning("The zone scoreboard source IP {SourceIp} isn't a valid address; no game packets will match.", settings.SourceIp);
            _sourceIps = [];
        }

        // A missing destination never matched before either, so keep it that way rather than
        // suddenly accepting the source's traffic to anywhere.
        _destinationIp = IPAddress.TryParse(settings.DestinationIp, out IPAddress? destinationIp) ? destinationIp : null;

        if (_destinationIp == null)
        {
            _logger.LogWarning("The zone scoreboard destination IP {DestinationIp} isn't a valid address; no game packets will match.", settings.DestinationIp);
        }
    }

    // Runs on SharpPcap's capture thread, one packet at a time. Packets are handled here rather
    // than on the thread pool so they reach the game state in the order the control box sent them:
    // a game start or end handled out of order would publish the wrong status edge.
    private void OnPacketArrival(object sender, PacketCapture e)
    {
        Interlocked.Exchange(ref _lastPacketTicks, DateTime.UtcNow.Ticks);

        try
        {
            RawCapture raw = e.GetPacket();
            Packet packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);

            UdpPacket? udpPacket = packet.Extract<UdpPacket>();

            if (udpPacket?.ParentPacket is not IPPacket ipPacket || !IsGamePacket(ipPacket))
            {
                return;
            }

            Interlocked.Exchange(ref _lastGamePacketTicks, DateTime.UtcNow.Ticks);

            byte[] rawBytes = udpPacket.PayloadData;

            if (rawBytes == null || rawBytes.Length == 0)
            {
                return;
            }

            string[] decodedData = LaserPacketDecoder.Decode(rawBytes);

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Game packet from {Source} to {Destination}: {Payload}", ipPacket.SourceAddress, ipPacket.DestinationAddress, string.Join(',', decodedData));
            }

            HandlePacket(decodedData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occured handling a packet.");
        }
    }

    private bool IsGamePacket(IPPacket ipPacket)
    {
        IPAddress? destinationIp = _destinationIp;

        if (destinationIp == null || !destinationIp.Equals(ipPacket.DestinationAddress))
        {
            return false;
        }

        foreach (IPAddress sourceIp in _sourceIps)
        {
            if (sourceIp.Equals(ipPacket.SourceAddress))
            {
                return true;
            }
        }

        return false;
    }

    public void HandlePacket(string[] decodedData)
    {
        if (decodedData.Length == 0 || !int.TryParse(decodedData[0], out int packetType))
        {
            _logger.LogDebug("Invalid number for packet type recieved: {value}", decodedData.FirstOrDefault());
            return;
        }

        if (decodedData.Length < LaserPacketDecoder.MinimumFieldCount(packetType))
        {
            _logger.LogDebug("Dropped a type {PacketType} packet with only {FieldCount} fields.", packetType, decodedData.Length);
            return;
        }

        switch (packetType)
        {
            case LaserPacketDecoder.TimingPacket:
                _actions.HandleTimingPacket(decodedData);
                break;
            case LaserPacketDecoder.TeamScorePacket:
                // DON'T CARE ABOUT THIS AS THE TEAM SCORE CAN ALREADY BE WORKED OUT WITH PLAYER SCORES
                break;
            case LaserPacketDecoder.PlayerScorePacket:
                _actions.HandlePlayerScorePacket(decodedData);
                break;
            case LaserPacketDecoder.GameStatusPacket:
                _actions.HandleGameStatusPacket(decodedData);
                break;
            case LaserPacketDecoder.ShotConfirmedPacket:
                _actions.HandleShotConfirmedPacket(decodedData);
                break;
        }
    }
}

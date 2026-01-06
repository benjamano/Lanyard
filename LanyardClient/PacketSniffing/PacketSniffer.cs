using Microsoft.Build.Framework;
using Microsoft.Extensions.Logging;
using PacketDotNet;
using SharpPcap;
using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Text;
using Lanyard.Client.PacketSniffing;

namespace Lanyard.Client.PacketSniffing;

public class PacketSniffer(ILogger<PacketSniffer> logger, Actions actions) : IPacketSniffer
{
    private readonly List<string> IPFilter = ["192.168.0.10", "192.168.0.11"];
    private readonly string BroadcastIP = "192.168.0.255";
    private readonly PhysicalAddress PreferedInterfaceMacAddress = PhysicalAddress.None;

    private readonly ILogger<PacketSniffer> _logger = logger;
    private readonly Actions _actions = actions;

    public void StartSniffing()
    {
        CaptureDeviceList devices = CaptureDeviceList.Instance;

        if (devices.Any() == false)
        {
            _logger.LogWarning("0 usable Packet Sniffing Devices were found!");

            return;
        }

        ILiveDevice? device = devices.Where(x => x.MacAddress == PreferedInterfaceMacAddress).FirstOrDefault();

        if (device == null && devices.Any())
        {
            _logger.LogWarning("Could not find the Prefered Interface with the MAC Address: {PreferedInterfaceMacAddress}, defaulting to first entry.", PreferedInterfaceMacAddress);

            device = devices.FirstOrDefault()!;
        }
        else if (device == null)
        {
            _logger.LogError("Could not find any interfaces to sniff!");

            return;
        }

        device.OnPacketArrival += OnPacketArrival;

        device.Open(new DeviceConfiguration
        {
            Mode = DeviceModes.Promiscuous,
            ReadTimeout = 1000
        });

        device.StartCapture();
    }

    private void OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            Packet packet = Packet.ParsePacket(e.GetPacket().LinkLayerType, e.GetPacket().Data);
            IPPacket ipPacket = packet.Extract<IPPacket>();

            if (ipPacket == null)
            {
                return;
            }

            if (IPFilter.Contains(ipPacket.SourceAddress.ToString())
                && ipPacket.DestinationAddress.ToString() == BroadcastIP)
            {
                _logger.LogInformation("Recieved a packet that aligns with the filters!, Source: {source}, Destination {dest}", ipPacket.SourceAddress.ToString(), ipPacket.DestinationAddress.ToString());

                UdpPacket udpPacket = packet.Extract<UdpPacket>();
                if (udpPacket == null)
                {
                    return;
                }

                byte[] rawBytes = udpPacket.PayloadData;

                if (rawBytes == null || rawBytes.Length == 0)
                {
                    return;
                }

                string hex = BitConverter.ToString(rawBytes).Replace("-", "");

                string[] decodedData = HexToAscii(hex).Split(",");

                HandlePacket(decodedData);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Error occured handling a packet: {ex}", ex.Message);
            _logger.LogTrace(ex.StackTrace);
        }
    }

    public void HandlePacket(string[] decodedData)
    {
        if (int.TryParse(decodedData[0].ToString(), out int packetType))
        {
            switch (packetType)
            {
                case 1:
                    // Timing Packet
                    Task.Run(() => _actions.HandleTimingPacketAsync(decodedData));
                    break;
                case 2:
                    // Team Score Packet
                    // DON'T CARE ABOUT THIS AS THE TEAM SCORE CAN ALREADY BE WORKED OUT WITH PLAYER SCORES
                    break;
                case 3:
                    // Player Score Packet
                    Task.Run(() => _actions.HandlePlayerScorePacketAsync(decodedData));
                    break;
                case 4:
                    // Game Status Packet
                    Task.Run(() => _actions.HandleGameStatusPacketAsync(decodedData));
                    break;
                case 5:
                    // Shot Confirmed Packet

                    break;
            }
        }
        else
        {
            _logger.LogWarning("Invalid number for packet type recieved: {value}", decodedData[0]);
        }
    }

    private static string HexToAscii(string hex)
    {
        StringBuilder sb = new();

        for (int i = 0; i < hex.Length; i += 2)
        {
            sb.Append(Convert.ToChar(Convert.ToByte(hex.Substring(i, 2), 16)));
        }

        return sb.ToString();
    }

}
using System.Net.NetworkInformation;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Infrastructure.DTO.ZoneScoreboard;

public class NetworkInterfaceDto
{
    public string PhysicalAddress { get; set; } = string.Empty;
    public string? Name { get; set; } = string.Empty;
}
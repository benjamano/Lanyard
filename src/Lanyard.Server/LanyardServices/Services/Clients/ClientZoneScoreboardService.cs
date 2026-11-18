using System.Net.NetworkInformation;
using Lanyard.Application.Services.Clients;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.ZoneScoreboard;
using Lanyard.Shared.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Clients;

public class ClientZoneScoreboardService(
    IDbContextFactory<ApplicationDbContext> factory,
    IHubContext<SignalRControlHub> hubContext,
    ILogger<ClientZoneScoreboardService> logger) : IClientZoneScoreboardService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IHubContext<SignalRControlHub> _hubContext = hubContext;
    private readonly ILogger<ClientZoneScoreboardService> _logger = logger;

    public static ZoneScoreboardSettingsDTO ToDto(ZoneScoreboardSettings settings) => new()
    {
        PreferredDeviceMacAddress = settings.PreferredDeviceMacAddress,
        ZoneScoreboardVersion = settings.ZoneScoreboardVersion,
        SourceIp = settings.SourceIp,
        DestinationIp = settings.DestinationIp,
        UseCaptureFilter = settings.UseCaptureFilter,
        PromiscuousCapture = settings.PromiscuousCapture,
    };

    public async Task<Result<ZoneScoreboardSettings?>> GetZoneScoreboardSettingsAsync(Guid clientId)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();
            ZoneScoreboardSettings? settings = await context.ZoneScoreboardSettings.FirstOrDefaultAsync(s => s.ClientId == clientId);

            return Result<ZoneScoreboardSettings?>.Ok(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving zone scoreboard settings for client {ClientId}", clientId);
            return Result<ZoneScoreboardSettings?>.Fail("Error retrieving zone scoreboard settings");
        }
    }

    public async Task<Result<List<ClientAvailableNetworkInterface>>> GetClientAvailableNetworkInterfacesAsync(Guid clientId)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            List<ClientAvailableNetworkInterface> interfaces = await context.ClientAvailableNetworkInterfaces
                .Where(i => i.ClientId == clientId)
                .Where(x=> x.IsActive)
                .ToListAsync();

            return Result<List<ClientAvailableNetworkInterface>>.Ok(interfaces);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving available network interfaces for client {ClientId}", clientId);
            return Result<List<ClientAvailableNetworkInterface>>.Fail("Error retrieving available network interfaces");

        }
    }

    public async Task<Result<bool>> UpdateClientAvailableNetworkInterfacesAsync(Guid clientId, IEnumerable<NetworkInterfaceDto> interfaces)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            List<ClientAvailableNetworkInterface> existingInterfaces = await context.ClientAvailableNetworkInterfaces
                .TagWithCallSite()
                .Where(i => i.ClientId == clientId)
                .Where(x=> x.IsActive)
                .ToListAsync();

            existingInterfaces.ForEach(i => i.IsActive = interfaces.Select(x=> x.PhysicalAddress).Contains(i.MacAddress.ToString()));

            List<ClientAvailableNetworkInterface> newInterfaces = interfaces
                .Where(x=> x.PhysicalAddress != PhysicalAddress.None.ToString())
                .Where(x=> !existingInterfaces.Select(e => e.MacAddress.ToString()).Contains(x.PhysicalAddress))
                .Select(i => new ClientAvailableNetworkInterface
                {
                    ClientId = clientId,
                    MacAddress = PhysicalAddress.Parse(i.PhysicalAddress),
                    Name = i.Name ?? "Unnamed Interface",
                    LastSeenDate = DateTime.UtcNow,
                    IsActive = true
                }).ToList();

            await context.ClientAvailableNetworkInterfaces.AddRangeAsync(newInterfaces);

            await context.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating available network interfaces for client {ClientId}", clientId);
            return Result<bool>.Fail("Error updating available network interfaces");
        }
    }

    public async Task<Result<bool>> UpdateZoneScoreboardSettingsAsync(ZoneScoreboardSettings settings)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            ZoneScoreboardSettings? existingSettings = await context.ZoneScoreboardSettings
                .FirstOrDefaultAsync(s => s.ClientId == settings.ClientId);

            if (existingSettings != null)
            {
                existingSettings.PreferredDeviceMacAddress = settings.PreferredDeviceMacAddress;
                existingSettings.ZoneScoreboardVersion = settings.ZoneScoreboardVersion;
                existingSettings.DestinationIp = settings.DestinationIp;
                existingSettings.SourceIp = settings.SourceIp;
                existingSettings.IsActive = settings.IsActive;
                existingSettings.UseCaptureFilter = settings.UseCaptureFilter;
                existingSettings.PromiscuousCapture = settings.PromiscuousCapture;

                context.ZoneScoreboardSettings.Update(existingSettings);
            }
            else
            {
                settings.ClientId = settings.ClientId;

                await context.ZoneScoreboardSettings.AddAsync(settings);
            }

            await context.SaveChangesAsync();

            await SendSettingsToConnectedKioskAsync(context, settings.ClientId, existingSettings ?? settings);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating zone scoreboard settings for client {ClientId}", settings.ClientId);
            return Result<bool>.Fail("Error updating zone scoreboard settings");
        }
    }

    // A connected kiosk applies new settings straight away, so turning the capture filter on or
    // off doesn't wait for its next reconnect. An offline kiosk gets them when it next connects.
    private async Task SendSettingsToConnectedKioskAsync(ApplicationDbContext context, Guid clientId, ZoneScoreboardSettings settings)
    {
        try
        {
            string? connectionId = await context.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == clientId)
                .Select(x => x.MostRecentConnectionId)
                .FirstOrDefaultAsync();

            if (string.IsNullOrEmpty(connectionId))
            {
                return;
            }

            await _hubContext.Clients.Client(connectionId).SendAsync(ClientHubMethods.ReceiveZoneScoreboardSettings, ToDto(settings));

            _logger.LogInformation("Sent updated zone scoreboard settings to client {ClientId}", clientId);
        }
        catch (Exception ex)
        {
            // Saved either way; the kiosk picks them up on its next connect.
            _logger.LogWarning(ex, "Saved zone scoreboard settings but couldn't send them to client {ClientId}", clientId);
        }
    }
}

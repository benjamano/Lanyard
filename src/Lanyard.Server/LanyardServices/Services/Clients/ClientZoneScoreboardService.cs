using System.Net.NetworkInformation;
using Lanyard.Application.Services.Clients;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.ZoneScoreboard;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Clients;

public class ClientZoneScoreboardService(IDbContextFactory<ApplicationDbContext> factory, ILogger<ClientZoneScoreboardService> logger) : IClientZoneScoreboardService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ILogger<ClientZoneScoreboardService> _logger = logger;

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
                .ToListAsync();

            return Result<List<ClientAvailableNetworkInterface>>.Ok(interfaces);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving available network interfaces for client {ClientId}", clientId);
            return Result<List<ClientAvailableNetworkInterface>>.Fail("Error retrieving available network interfaces");

        }
    }

    public async Task<Result<bool>> UpdateClientAvailableNetworkInterfacesAsync(Guid clientId, IEnumerable<PhysicalAddress> interfaces)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            List<ClientAvailableNetworkInterface> existingInterfaces = await context.ClientAvailableNetworkInterfaces
                .Where(i => i.ClientId == clientId)
                .ToListAsync();

            await context.ClientAvailableNetworkInterfaces
                .Where(x=> x.ClientId == clientId)
                .ForEachAsync(x=> x.IsActive = false);

            List<ClientAvailableNetworkInterface> newInterfaces = interfaces
                .Select(i => new ClientAvailableNetworkInterface
                {
                    ClientId = clientId,
                    MacAddress = i
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

                context.ZoneScoreboardSettings.Update(existingSettings);
            }
            else
            {
                settings.ClientId = settings.ClientId;

                await context.ZoneScoreboardSettings.AddAsync(settings);
            }

            await context.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating zone scoreboard settings for client {ClientId}", settings.ClientId);
            return Result<bool>.Fail("Error updating zone scoreboard settings");
        }
    }
}

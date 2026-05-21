using Lanyard.Application.Services.Authentication;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Dmx;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

public class DmxSceneService(
    IDbContextFactory<ApplicationDbContext> factory, 
    IHubContext<SignalRControlHub> hubContext, 
    ILogger<DmxSceneService> logger, 
    IMemoryCache cache,
    IDmxService dmxService,
    ISecurityService securityService) : IDmxSceneService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IHubContext<SignalRControlHub> _hubContext = hubContext;
    private readonly ILogger<DmxSceneService> _logger = logger;
    private readonly IMemoryCache _cache = cache;
    private readonly IDmxService _dmxService = dmxService;
    private readonly ISecurityService _securityService = securityService;

    public event Action<Guid, Guid>? OnSceneStarted;
    public event Action<Guid, Guid>? OnSceneStopped;

    public async Task<Result<IEnumerable<DmxSceneDTO>>> GetScenesForClientAsync(Guid clientId)
    {
        try
        {
            using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            List<Guid> runningSceneIds = (await GetRunningSceneIdsForClientAsync(clientId)).Data ?? [];

            IEnumerable<DmxSceneDTO> scenes = await context.DmxScenes
                .Where(s => s.ClientId == clientId)
                .Select(s => new DmxSceneDTO
                {
                    Id = s.Id,
                    Name = s.Name,
                    ClientId = s.ClientId,
                    CreateByUserId = s.CreateByUserId,
                    CreateDate = s.CreateDate,
                    IsRunning = runningSceneIds.Contains(s.Id)
                })
                .ToListAsync();

            return Result<IEnumerable<DmxSceneDTO>>.Ok(scenes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving DMX scenes for client {ClientId}", clientId);
            return Result<IEnumerable<DmxSceneDTO>>.Fail("An error occurred while retrieving DMX scenes.");
        }
    }

    private async Task<Result<List<Guid>>> GetRunningSceneIdsForClientAsync(Guid clientId)
    {
        try
        {
            List<Guid> runningSceneIds = [];

            return Result<List<Guid>>.Ok(runningSceneIds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving running DMX scene IDs for client {ClientId}", clientId);
            return Result<List<Guid>>.Fail("An error occurred while retrieving running DMX scene IDs.");
        }
    }

    public async Task<Result<DmxScene>> CreateSceneAsync(Guid clientId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<DmxScene>.Fail("Scene name cannot be empty.");
        }

        string currentUserId = await _securityService.GetCurrentUserIdAsync().ContinueWith(x => x.Result.Data!);
        
        try
        {
            using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxScene newScene = new()
            {
                Name = name,
                ClientId = clientId,
                CreateByUserId = currentUserId!,
                CreateDate = DateTime.Now,
            };

            context.DmxScenes.Add(newScene);

            await context.SaveChangesAsync();

            return Result<DmxScene>.Ok(newScene);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating DMX scene for client {ClientId}", clientId);

            return Result<DmxScene>.Fail("An error occurred while creating the DMX scene.");
        }
    }
}
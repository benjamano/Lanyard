using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Dmx;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

// Scoped, on the tenant-filtered factory: every scene, step and kiosk it can see belongs to the
// caller's company, so loading a row through it is the ownership check.
public class DmxSceneService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<DmxSceneService> logger,
    IDmxSceneRunnerService sceneRunner,
    ISecurityService securityService) : IDmxSceneService
{
    public const int MaxSceneNameLength = 100;
    public const int MaxStepNameLength = 60;
    public static readonly TimeSpan MaxStepDuration = TimeSpan.FromHours(1);

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ILogger<DmxSceneService> _logger = logger;
    private readonly IDmxSceneRunnerService _sceneRunner = sceneRunner;
    private readonly ISecurityService _securityService = securityService;

    public async Task<Result<IEnumerable<DmxSceneDTO>>> GetScenesForClientAsync(Guid clientId)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            List<Guid> runningSceneIds = _sceneRunner.GetRunningSceneIds(clientId);

            // List callers (scene selector, automation rule dialog) only read StepCount, so
            // the steps themselves are counted in SQL rather than loaded (and tracked) per scene.
            List<DmxSceneDTO> scenes = await context.DmxScenes
                .AsNoTracking()
                .TagWithCallSite()
                .Where(s => s.ClientId == clientId && s.IsActive)
                .OrderBy(s => s.Name)
                .ThenBy(s => s.CreateDate)
                .Select(s => new DmxSceneDTO
                {
                    Id = s.Id,
                    Name = s.Name,
                    ClientId = s.ClientId,
                    CompanyId = s.CompanyId,
                    Loop = s.Loop,
                    IsMomentary = s.IsMomentary,
                    BpmSyncEnabled = s.BpmSyncEnabled,
                    KeyBindings = s.KeyBindings,
                    IsActive = s.IsActive,
                    CreateByUserId = s.CreateByUserId,
                    CreateDate = s.CreateDate,
                    StepCount = s.Steps.Count
                })
                .ToListAsync();

            foreach (DmxSceneDTO scene in scenes)
            {
                scene.IsRunning = runningSceneIds.Contains(scene.Id);
            }

            return Result<IEnumerable<DmxSceneDTO>>.Ok(scenes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving DMX scenes for client {ClientId}", clientId);
            return Result<IEnumerable<DmxSceneDTO>>.Fail("An error occurred while retrieving DMX scenes.");
        }
    }

    public async Task<Result<DmxScene>> CreateSceneAsync(Guid clientId, string name)
    {
        string? nameError = ValidateName(name);

        if (nameError != null)
        {
            return Result<DmxScene>.Fail(nameError);
        }

        try
        {
            Result<string> user = await _securityService.GetCurrentUserIdAsync();

            if (!user.IsSuccess || string.IsNullOrEmpty(user.Data))
            {
                return Result<DmxScene>.Fail("Couldn't work out who you are. Sign in again and retry.");
            }

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            // Tenant-filtered: a kiosk from another company is simply not found.
            bool clientVisible = await context.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(c => c.Id == clientId);

            if (!clientVisible)
            {
                return Result<DmxScene>.Fail("Kiosk not found.");
            }

            DmxScene newScene = new()
            {
                Name = name.Trim(),
                ClientId = clientId,
                IsActive = true,
                CreateByUserId = user.Data,
                CreateDate = DateTime.UtcNow,
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

    public async Task<Result<DmxSceneDTO>> GetSceneByIdAsync(Guid sceneId, Guid? clientId = null)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxSceneDTO? scene = await context.DmxScenes
                .AsNoTracking()
                .TagWithCallSite()
                .Where(s => s.Id == sceneId && s.IsActive)
                .Select(s => new DmxSceneDTO
                {
                    Id = s.Id,
                    Name = s.Name,
                    ClientId = s.ClientId,
                    CompanyId = s.CompanyId,
                    Loop = s.Loop,
                    IsMomentary = s.IsMomentary,
                    BpmSyncEnabled = s.BpmSyncEnabled,
                    KeyBindings = s.KeyBindings,
                    IsActive = s.IsActive,
                    CreateByUserId = s.CreateByUserId,
                    CreateDate = s.CreateDate,
                    StepCount = s.Steps.Count
                })
                .FirstOrDefaultAsync();

            if (scene == null)
            {
                return Result<DmxSceneDTO>.Fail("Scene not found.");
            }

            // The scene knows its own kiosk, so callers no longer need to pass one for this.
            scene.IsRunning = _sceneRunner.GetRunningSceneIds(clientId ?? scene.ClientId).Contains(scene.Id);

            return Result<DmxSceneDTO>.Ok(scene);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving DMX scene {SceneId}", sceneId);
            return Result<DmxSceneDTO>.Fail("An error occurred while retrieving the DMX scene.");
        }
    }

    public async Task<Result<bool>> UpdateSceneAsync(DmxScene scene)
    {
        string? nameError = ValidateName(scene.Name);

        if (nameError != null)
        {
            return Result<bool>.Fail(nameError);
        }

        try
        {
            Result<string> user = await _securityService.GetCurrentUserIdAsync();

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxScene? existingScene = await context.DmxScenes
                .TagWithCallSite()
                .Where(s => s.Id == scene.Id && s.IsActive)
                .FirstOrDefaultAsync();

            if (existingScene == null)
            {
                return Result<bool>.Fail("Scene not found.");
            }

            // Loop/momentary are baked into a run at start, so a running scene would keep the
            // old behaviour; stop it like any other edit. Renaming or rebinding keys doesn't.
            bool playbackChanged = existingScene.Loop != scene.Loop
                || existingScene.IsMomentary != scene.IsMomentary
                || existingScene.BpmSyncEnabled != scene.BpmSyncEnabled;

            existingScene.Name = scene.Name.Trim();
            existingScene.Loop = scene.Loop;
            existingScene.IsMomentary = scene.IsMomentary;
            existingScene.BpmSyncEnabled = scene.BpmSyncEnabled;
            existingScene.KeyBindings = scene.KeyBindings;
            existingScene.UpdateByUserId = user.IsSuccess ? user.Data : null;
            existingScene.UpdateDate = DateTime.UtcNow;

            await context.SaveChangesAsync();

            if (playbackChanged)
            {
                _sceneRunner.StopScene(existingScene.Id);
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating DMX scene {SceneId}", scene.Id);
            return Result<bool>.Fail("An error occurred while updating the DMX scene.");
        }
    }

    public async Task<Result<bool>> DeleteSceneAsync(Guid sceneId)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxScene? scene = await context.DmxScenes
                .TagWithCallSite()
                .Where(s => s.Id == sceneId && s.IsActive)
                .FirstOrDefaultAsync();

            if (scene == null)
            {
                return Result<bool>.Fail("Scene not found.");
            }

            scene.IsActive = false;
            await context.SaveChangesAsync();

            // Only once we know it's this company's scene. A deleted scene must not keep playing.
            _sceneRunner.StopScene(sceneId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting DMX scene {SceneId}", sceneId);
            return Result<bool>.Fail("An error occurred while deleting the DMX scene.");
        }
    }

    public async Task<Result<List<DmxSceneStep>>> GetSceneStepsAsync(Guid sceneId)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            List<DmxSceneStep> steps = await context.DmxSceneSteps
                .AsNoTracking()
                .TagWithCallSite()
                .Where(s => s.SceneId == sceneId && s.Scene!.IsActive)
                .OrderBy(s => s.StepNumber)
                .Include(s => s.ChannelValues)
                .ToListAsync();

            return Result<List<DmxSceneStep>>.Ok(steps);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving DMX scene steps for scene {SceneId}", sceneId);
            return Result<List<DmxSceneStep>>.Fail("An error occurred while retrieving DMX scene steps.");
        }
    }

    public async Task<Result<bool>> DeleteSceneStepAsync(Guid stepId)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxSceneStep? step = await context.DmxSceneSteps
                .TagWithCallSite()
                .Where(s => s.Id == stepId)
                .FirstOrDefaultAsync();

            if (step == null)
            {
                return Result<bool>.Fail("Scene step not found.");
            }

            // Steps are child rows of the scene (the scene itself is soft-deleted), so a removed
            // step goes for good. The rest close up behind it, keeping numbers 1..N with no gaps:
            // progress is shown as number / count, which used to pass 100% after a delete.
            List<DmxSceneStep> later = await context.DmxSceneSteps
                .TagWithCallSite()
                .Where(s => s.SceneId == step.SceneId && s.StepNumber > step.StepNumber)
                .ToListAsync();

            context.DmxSceneSteps.Remove(step);

            foreach (DmxSceneStep laterStep in later)
            {
                laterStep.StepNumber--;
            }

            await context.SaveChangesAsync();

            // The runner snapshots steps at start, so stop the scene rather than let
            // it keep playing a stale definition; the operator restarts to pick up the change.
            _sceneRunner.StopScene(step.SceneId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting DMX scene step {StepId}", stepId);
            return Result<bool>.Fail("An error occurred while deleting the DMX scene step.");
        }
    }

    public async Task<Result<DmxSceneStep>> CreateSceneStepAsync(Guid sceneId)
    {
        try
        {
            Result<string> user = await _securityService.GetCurrentUserIdAsync();

            if (!user.IsSuccess || string.IsNullOrEmpty(user.Data))
            {
                return Result<DmxSceneStep>.Fail("Couldn't work out who you are. Sign in again and retry.");
            }

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            bool sceneVisible = await context.DmxScenes
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(s => s.Id == sceneId && s.IsActive);

            if (!sceneVisible)
            {
                return Result<DmxSceneStep>.Fail("Scene not found.");
            }

            int nextStepNumber = await context.DmxSceneSteps
                .TagWithCallSite()
                .Where(s => s.SceneId == sceneId)
                .MaxAsync(s => (int?)s.StepNumber) ?? 0;

            DmxSceneStep newStep = new()
            {
                SceneId = sceneId,
                StepNumber = nextStepNumber + 1,
                Name = $"Step {nextStepNumber + 1}",
                Duration = TimeSpan.FromSeconds(5),
                Beats = 1,
                CreateByUserId = user.Data,
                CreateDate = DateTime.UtcNow
            };

            context.DmxSceneSteps.Add(newStep);
            await context.SaveChangesAsync();

            _sceneRunner.StopScene(sceneId);

            return Result<DmxSceneStep>.Ok(newStep);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating DMX scene step for scene {SceneId}", sceneId);
            return Result<DmxSceneStep>.Fail("An error occurred while creating the DMX scene step.");
        }
    }

    public async Task<Result<bool>> UpdateSceneStepDurationAsync(Guid stepId, TimeSpan newDuration)
    {
        // A zero step on a looping scene used to spin the runner as fast as it could go.
        if (newDuration <= TimeSpan.Zero || newDuration > MaxStepDuration)
        {
            return Result<bool>.Fail("A step must last more than 0 seconds and no more than an hour.");
        }

        return await UpdateStepAsync(stepId, step => step.Duration = newDuration, "duration");
    }

    public async Task<Result<bool>> UpdateSceneStepFadeAsync(Guid stepId, TimeSpan fade)
    {
        if (fade < TimeSpan.Zero || fade > MaxStepDuration)
        {
            return Result<bool>.Fail("A fade must be between 0 seconds and an hour.");
        }

        return await UpdateStepAsync(stepId, step => step.FadeTime = fade, "fade");
    }

    public async Task<Result<bool>> UpdateSceneStepBeatsAsync(Guid stepId, double beats)
    {
        // Same clamp as the editor UI: fractions give sub-beat chases, 64 covers
        // any realistic multi-bar hold.
        if (double.IsNaN(beats) || beats is < 0.25 or > 64)
        {
            return Result<bool>.Fail("Beats must be between 0.25 and 64.");
        }

        return await UpdateStepAsync(stepId, step => step.Beats = beats, "beats");
    }

    private async Task<Result<bool>> UpdateStepAsync(Guid stepId, Action<DmxSceneStep> apply, string what)
    {
        try
        {
            Result<string> user = await _securityService.GetCurrentUserIdAsync();

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxSceneStep? step = await context.DmxSceneSteps
                .TagWithCallSite()
                .Where(s => s.Id == stepId && s.Scene!.IsActive)
                .FirstOrDefaultAsync();

            if (step == null)
            {
                return Result<bool>.Fail("Scene step not found.");
            }

            apply(step);
            step.UpdateByUserId = user.IsSuccess ? user.Data : null;
            step.UpdateDate = DateTime.UtcNow;

            await context.SaveChangesAsync();

            _sceneRunner.StopScene(step.SceneId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating {What} for DMX scene step {StepId}", what, stepId);
            return Result<bool>.Fail($"An error occurred while updating the DMX scene step {what}.");
        }
    }

    public Task<Result<bool>> SaveSceneStepChannelValueAsync(Guid stepId, int channelNumber, byte value)
    {
        return SaveSceneStepChannelValuesAsync(stepId, [new DmxChannel { Address = channelNumber, Value = value }]);
    }

    public async Task<Result<bool>> SaveSceneStepChannelValuesAsync(Guid stepId, IReadOnlyList<DmxChannel> values)
    {
        if (values.Count == 0)
        {
            return Result<bool>.Ok(true);
        }

        if (values.Any(v => v.Address is < 1 or > DmxService.UniverseSize))
        {
            return Result<bool>.Fail("DMX channels are numbered 1 to 512.");
        }

        try
        {
            Result<string> user = await _securityService.GetCurrentUserIdAsync();

            if (!user.IsSuccess || string.IsNullOrEmpty(user.Data))
            {
                return Result<bool>.Fail("Couldn't work out who you are. Sign in again and retry.");
            }

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            // Loaded through the tenant filter. The value rows aren't company-owned themselves,
            // so without this a step id from another company could be written to.
            DmxSceneStep? step = await context.DmxSceneSteps
                .AsNoTracking()
                .TagWithCallSite()
                .Where(s => s.Id == stepId && s.Scene!.IsActive)
                .FirstOrDefaultAsync();

            if (step == null)
            {
                return Result<bool>.Fail("Scene step not found.");
            }

            // Latest value wins if the same channel appears twice in one batch.
            Dictionary<int, byte> wanted = [];

            foreach (DmxChannel value in values)
            {
                wanted[value.Address] = value.Value;
            }

            List<int> channels = [.. wanted.Keys];

            Dictionary<int, DmxSceneStepChannelValue> existing = await context.DmxSceneStepChannelValues
                .TagWithCallSite()
                .Where(c => c.SceneStepId == stepId && channels.Contains(c.ChannelNumber))
                .ToDictionaryAsync(c => c.ChannelNumber);

            DateTime now = DateTime.UtcNow;

            foreach ((int channel, byte value) in wanted)
            {
                if (existing.TryGetValue(channel, out DmxSceneStepChannelValue? row))
                {
                    if (row.Value != value)
                    {
                        row.Value = value;
                        row.UpdateByUserId = user.Data;
                        row.UpdateDate = now;
                    }
                }
                else
                {
                    context.DmxSceneStepChannelValues.Add(new DmxSceneStepChannelValue
                    {
                        SceneStepId = stepId,
                        ChannelNumber = channel,
                        Value = value,
                        CreateByUserId = user.Data,
                        CreateDate = now
                    });
                }
            }

            await context.SaveChangesAsync();

            _sceneRunner.StopScene(step.SceneId);

            return Result<bool>.Ok(true);
        }
        catch (DbUpdateException ex)
        {
            // The unique (step, channel) index caught two saves racing to insert the same channel.
            // The other one landed; the operator's next nudge of the fader will update it.
            _logger.LogWarning(ex, "Concurrent save of DMX scene step {StepId} channel values; keeping the one that landed first", stepId);
            return Result<bool>.Fail("Another save of this step landed at the same time. Move the fader again to update it.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving {ChannelCount} DMX scene step channel values for step {StepId}", values.Count, stepId);
            return Result<bool>.Fail("An error occurred while saving the DMX scene step channel values.");
        }
    }

    public async Task<Result<bool>> StopAllScenesForClientAsync(Guid clientId)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            bool clientVisible = await context.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(c => c.Id == clientId);

            if (!clientVisible)
            {
                return Result<bool>.Fail("Kiosk not found.");
            }

            return _sceneRunner.StopAllScenesForClient(clientId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping all DMX scenes for client {ClientId}", clientId);
            return Result<bool>.Fail("An error occurred while stopping DMX scenes.");
        }
    }

    public async Task<Result<bool>> RenameStepAsync(Guid stepId, string name)
    {
        string trimmed = (name ?? string.Empty).Trim();

        if (trimmed.Length == 0 || trimmed.Length > MaxStepNameLength)
        {
            return Result<bool>.Fail($"Step names need 1 to {MaxStepNameLength} characters.");
        }

        // A rename doesn't change playback, so it doesn't stop a running scene.
        try
        {
            Result<string> user = await _securityService.GetCurrentUserIdAsync();

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxSceneStep? step = await context.DmxSceneSteps
                .TagWithCallSite()
                .FirstOrDefaultAsync(s => s.Id == stepId && s.Scene!.IsActive);

            if (step == null)
            {
                return Result<bool>.Fail("Scene step not found.");
            }

            step.Name = trimmed;
            step.UpdateByUserId = user.IsSuccess ? user.Data : null;
            step.UpdateDate = DateTime.UtcNow;

            await context.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renaming DMX scene step {StepId}", stepId);
            return Result<bool>.Fail("An error occurred while renaming the step.");
        }
    }

    public async Task<Result<DmxSceneStep>> DuplicateStepAsync(Guid stepId)
    {
        try
        {
            Result<string> user = await _securityService.GetCurrentUserIdAsync();

            if (!user.IsSuccess || string.IsNullOrEmpty(user.Data))
            {
                return Result<DmxSceneStep>.Fail("Couldn't work out who you are. Sign in again and retry.");
            }

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxSceneStep? source = await context.DmxSceneSteps
                .AsNoTracking()
                .TagWithCallSite()
                .Include(s => s.ChannelValues)
                .FirstOrDefaultAsync(s => s.Id == stepId && s.Scene!.IsActive);

            if (source == null)
            {
                return Result<DmxSceneStep>.Fail("Scene step not found.");
            }

            List<DmxSceneStep> later = await context.DmxSceneSteps
                .TagWithCallSite()
                .Where(s => s.SceneId == source.SceneId && s.StepNumber > source.StepNumber)
                .ToListAsync();

            foreach (DmxSceneStep step in later)
            {
                step.StepNumber++;
            }

            DateTime now = DateTime.UtcNow;
            Guid copyId = Guid.NewGuid();

            DmxSceneStep copy = new()
            {
                Id = copyId,
                SceneId = source.SceneId,
                StepNumber = source.StepNumber + 1,
                Name = Truncate($"{source.Name} copy", MaxStepNameLength),
                Duration = source.Duration,
                Beats = source.Beats,
                FadeTime = source.FadeTime,
                CreateByUserId = user.Data,
                CreateDate = now,
                ChannelValues = source.ChannelValues
                    .Select(v => new DmxSceneStepChannelValue
                    {
                        SceneStepId = copyId,
                        ChannelNumber = v.ChannelNumber,
                        Value = v.Value,
                        CreateByUserId = user.Data,
                        CreateDate = now
                    })
                    .ToList()
            };

            context.DmxSceneSteps.Add(copy);
            await context.SaveChangesAsync();

            _sceneRunner.StopScene(source.SceneId);

            return Result<DmxSceneStep>.Ok(copy);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error duplicating DMX scene step {StepId}", stepId);
            return Result<DmxSceneStep>.Fail("An error occurred while duplicating the step.");
        }
    }

    public async Task<Result<bool>> ReorderStepsAsync(Guid sceneId, IReadOnlyList<Guid> orderedStepIds)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            List<DmxSceneStep> steps = await context.DmxSceneSteps
                .TagWithCallSite()
                .Where(s => s.SceneId == sceneId && s.Scene!.IsActive)
                .ToListAsync();

            if (steps.Count == 0)
            {
                return Result<bool>.Fail("Scene not found.");
            }

            // Every step exactly once: a stale page mustn't drop or duplicate steps.
            if (orderedStepIds.Count != steps.Count || orderedStepIds.Distinct().Count() != steps.Count
                || orderedStepIds.Any(id => steps.All(s => s.Id != id)))
            {
                return Result<bool>.Fail("The steps have changed since the page loaded. Reload and try again.");
            }

            Dictionary<Guid, DmxSceneStep> byId = steps.ToDictionary(s => s.Id);

            for (int i = 0; i < orderedStepIds.Count; i++)
            {
                byId[orderedStepIds[i]].StepNumber = i + 1;
            }

            await context.SaveChangesAsync();

            _sceneRunner.StopScene(sceneId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reordering steps of DMX scene {SceneId}", sceneId);
            return Result<bool>.Fail("An error occurred while reordering the steps.");
        }
    }

    public async Task<Result<bool>> ReplaceStepChannelValuesAsync(Guid stepId, IReadOnlyList<DmxChannel> values)
    {
        if (values.Any(v => v.Address is < 1 or > DmxService.UniverseSize))
        {
            return Result<bool>.Fail("DMX channels are numbered 1 to 512.");
        }

        try
        {
            Result<string> user = await _securityService.GetCurrentUserIdAsync();

            if (!user.IsSuccess || string.IsNullOrEmpty(user.Data))
            {
                return Result<bool>.Fail("Couldn't work out who you are. Sign in again and retry.");
            }

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxSceneStep? step = await context.DmxSceneSteps
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(s => s.Id == stepId && s.Scene!.IsActive);

            if (step == null)
            {
                return Result<bool>.Fail("Scene step not found.");
            }

            List<DmxSceneStepChannelValue> existing = await context.DmxSceneStepChannelValues
                .TagWithCallSite()
                .Where(v => v.SceneStepId == stepId)
                .ToListAsync();

            context.DmxSceneStepChannelValues.RemoveRange(existing);

            DateTime now = DateTime.UtcNow;

            foreach (DmxChannel value in values.GroupBy(v => v.Address).Select(g => g.Last()))
            {
                context.DmxSceneStepChannelValues.Add(new DmxSceneStepChannelValue
                {
                    SceneStepId = stepId,
                    ChannelNumber = value.Address,
                    Value = value.Value,
                    CreateByUserId = user.Data,
                    CreateDate = now
                });
            }

            await context.SaveChangesAsync();

            _sceneRunner.StopScene(step.SceneId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error replacing channel values of DMX scene step {StepId}", stepId);
            return Result<bool>.Fail("An error occurred while saving the step.");
        }
    }

    public async Task<Result<bool>> ClearStepChannelAsync(Guid stepId, int channelNumber)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            // Loaded through the tenant filter (via the step's scene), so only this company's rows.
            DmxSceneStepChannelValue? value = await context.DmxSceneStepChannelValues
                .TagWithCallSite()
                .Include(v => v.SceneStep)
                .FirstOrDefaultAsync(v => v.SceneStepId == stepId && v.ChannelNumber == channelNumber && v.SceneStep!.Scene!.IsActive);

            if (value == null)
            {
                return Result<bool>.Ok(true);
            }

            Guid sceneId = value.SceneStep!.SceneId;

            context.DmxSceneStepChannelValues.Remove(value);
            await context.SaveChangesAsync();

            _sceneRunner.StopScene(sceneId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing channel {ChannelNumber} from DMX scene step {StepId}", channelNumber, stepId);
            return Result<bool>.Fail("An error occurred while clearing the channel.");
        }
    }

    public async Task<Result<IReadOnlyList<string>>> GetAutomationRulesUsingSceneAsync(Guid sceneId)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            string id = sceneId.ToString();

            // Cheap text filter in SQL, then an exact read of each candidate's parameters.
            var candidates = await context.AutomationRuleActions
                .AsNoTracking()
                .TagWithCallSite()
                .Where(a => a.IsActive && a.ActionType == AutomationActionTypes.DmxSceneControl
                    && a.AutomationRule!.IsActive && a.ParametersJson.Contains(id))
                .Select(a => new { a.ParametersJson, RuleName = a.AutomationRule!.Name })
                .ToListAsync();

            List<string> names = candidates
                .Where(c => DmxSceneControlActionExecutor.TryReadTargets(c.ParametersJson, out _, out Guid? scene) && scene == sceneId)
                .Select(c => c.RuleName)
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            return Result<IReadOnlyList<string>>.Ok(names);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding automation rules that use DMX scene {SceneId}", sceneId);
            return Result<IReadOnlyList<string>>.Fail("An error occurred while checking automation rules.");
        }
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    private static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Scene name cannot be empty.";
        }

        return name.Trim().Length > MaxSceneNameLength
            ? $"Scene names can be at most {MaxSceneNameLength} characters."
            : null;
    }
}

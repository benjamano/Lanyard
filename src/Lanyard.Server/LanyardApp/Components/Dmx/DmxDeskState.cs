using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;

namespace Lanyard.App.Components.Dmx;

/// <summary>Whether the desk's faders drive the lights.</summary>
public enum DmxDeskOutput
{
    /// <summary>Faders drive the lights; the desk follows them as scenes and other desks change them.</summary>
    Live,

    /// <summary>Faders change only the desk; the lights carry on until the changes are sent.</summary>
    Blind
}

/// <summary>
/// Everything the DMX desk page shows for one kiosk: the channel buffer, Live/Blind, the lock, the
/// fixtures and their patch, master/blackout, the scene step being edited and the scene preview.
/// One per page, owned by DmxDesk; child views read it and call into it.
/// </summary>
/// <remarks>
/// <para>Every mutation runs on the page's dispatcher - live events from the engine are marshalled
/// in through <c>dispatch</c> - so renders never race writers (the old desk wrote its dictionaries
/// from the scene runner's thread while rendering them).</para>
/// <para><see cref="Changed"/> is raised after each change; the page coalesces those into at most
/// one render per 50 ms. <see cref="Version"/> increments with every change so child views can
/// cheaply tell whether anything moved.</para>
/// </remarks>
public sealed class DmxDeskState(
    IDmxDeskService desk,
    IDmxFixtureService fixtureService,
    IDmxSceneService sceneService,
    Func<Func<Task>, Task> dispatch,
    Action<string> showError) : IAsyncDisposable
{
    public const int UniverseSize = 512;
    private const int StepSaveDebounceMs = 300;

    public Guid? ClientId { get; private set; }

    public DmxDeskOutput Output { get; private set; } = DmxDeskOutput.Live;

    /// <summary>Locked faders and controls can't be moved (a phone in a pocket can't change the lights).</summary>
    public bool IsLocked { get; set; }

    /// <summary>What the desk shows: live levels, the blind buffer, or the step being edited. Index = address.</summary>
    public byte[] Values { get; private set; } = new byte[UniverseSize + 1];

    public DmxUniverseView ValuesView { get; private set; } = new(new byte[UniverseSize + 1]);

    /// <summary>Blind changes not yet sent to the lights.</summary>
    public bool HasUnsentChanges { get; private set; }

    public IReadOnlyList<DmxFixture> Fixtures { get; private set; } = [];

    public Dictionary<int, List<DmxChannelPatch>> Patches { get; private set; } = [];

    public bool FixturesLoaded { get; private set; }

    public DmxOutputState OutputState { get; private set; } = new(255, false);

    public DmxScene? EditingScene { get; private set; }

    public DmxSceneStep? EditingStep { get; private set; }

    public bool IsEditingStep => EditingStep != null;

    public int Version { get; private set; }

    public event Action? Changed;

    // Live events only apply while the desk is showing the live universe.
    private bool FollowsLive => !IsEditingStep && Output == DmxDeskOutput.Live;

    private IDisposable? _subscription;

    private readonly Dictionary<Guid, Dictionary<int, byte>> _pendingStepSaves = [];
    private CancellationTokenSource? _stepSaveDebounce;

    public IReadOnlyList<DmxChannelPatch> PatchesFor(int address) =>
        Patches.TryGetValue(address, out List<DmxChannelPatch>? patches) ? patches : Array.Empty<DmxChannelPatch>();

    private void Touch()
    {
        Version++;
        Changed?.Invoke();
    }

    private void ReplaceValues(byte[] values)
    {
        Values = values;
        ValuesView = new DmxUniverseView(values);
    }

    public async Task SetClientAsync(Guid? clientId)
    {
        if (clientId == ClientId && FixturesLoaded)
        {
            return;
        }

        await FlushStepSavesAsync();

        _subscription?.Dispose();
        _subscription = null;

        ClientId = clientId;
        EditingScene = null;
        EditingStep = null;
        Output = DmxDeskOutput.Live;
        HasUnsentChanges = false;
        Fixtures = [];
        Patches = [];
        FixturesLoaded = false;
        OutputState = new DmxOutputState(255, false);
        ReplaceValues(new byte[UniverseSize + 1]);

        if (clientId is Guid id)
        {
            Result<IDisposable> subscription = await desk.SubscribeToChannelsAsync(id,
                channels => _ = dispatch(() => ApplyLive(id, channels)),
                output => _ = dispatch(() => ApplyOutput(id, output)));

            if (subscription.IsSuccess)
            {
                _subscription = subscription.Data;
            }
            else
            {
                showError($"Couldn't follow the kiosk's lights: {subscription.Error}");
            }

            await Task.WhenAll(ReloadLiveAsync(), RefreshFixturesAsync(), LoadOutputStateAsync());
        }

        Touch();
    }

    private Task ApplyLive(Guid clientId, IReadOnlyList<DmxChannel> channels)
    {
        if (clientId != ClientId || !FollowsLive)
        {
            return Task.CompletedTask;
        }

        foreach (DmxChannel channel in channels)
        {
            Values[channel.Address] = channel.Value;
        }

        Touch();
        return Task.CompletedTask;
    }

    private Task ApplyOutput(Guid clientId, DmxOutputState output)
    {
        if (clientId == ClientId)
        {
            OutputState = output;
            Touch();
        }

        return Task.CompletedTask;
    }

    private async Task ReloadLiveAsync()
    {
        if (ClientId is not Guid id)
        {
            return;
        }

        Result<IReadOnlyList<DmxChannel>> result = await desk.GetChannelsAsync(id);

        if (!result.IsSuccess || result.Data == null)
        {
            showError($"Couldn't read the kiosk's channels: {result.Error}");
            return;
        }

        byte[] values = new byte[UniverseSize + 1];

        foreach (DmxChannel channel in result.Data)
        {
            if (channel.Address is >= 1 and <= UniverseSize)
            {
                values[channel.Address] = channel.Value;
            }
        }

        ReplaceValues(values);
    }

    private async Task LoadOutputStateAsync()
    {
        if (ClientId is not Guid id)
        {
            return;
        }

        Result<DmxOutputState> result = await desk.GetOutputStateAsync(id);

        if (result.IsSuccess && result.Data != null)
        {
            OutputState = result.Data;
        }
    }

    /// <summary>Re-reads the kiosk's fixtures, after the fixture manager changes them.</summary>
    public async Task RefreshFixturesAsync()
    {
        if (ClientId is not Guid id)
        {
            return;
        }

        Result<IEnumerable<DmxFixture>> result = await fixtureService.GetFixturesForClientAsync(id);

        if (result.IsSuccess && result.Data != null)
        {
            Fixtures = result.Data.OrderBy(f => f.StartChannel).ThenBy(f => f.Name).ToList();
        }
        else
        {
            Fixtures = [];
            showError($"Couldn't load the kiosk's fixtures: {result.Error}");
        }

        Patches = DmxChannelPatch.Build(Fixtures);
        FixturesLoaded = true;
        Touch();
    }

    public Task SetChannelAsync(int address, byte value) =>
        SetChannelsAsync([new DmxChannel { Address = address, Value = value }]);

    /// <summary>
    /// A fader, card or button moved. Live: sent to the lights. Blind: kept on the desk. Editing a
    /// step: saved to the step (batched), and sent to the lights too when Live.
    /// </summary>
    public async Task SetChannelsAsync(IReadOnlyList<DmxChannel> channels)
    {
        if (ClientId is not Guid id || IsLocked || channels.Count == 0)
        {
            return;
        }

        foreach (DmxChannel channel in channels)
        {
            if (channel.Address is >= 1 and <= UniverseSize)
            {
                Values[channel.Address] = channel.Value;
            }
        }

        if (EditingStep is DmxSceneStep step)
        {
            foreach (DmxChannel channel in channels)
            {
                SyncStepValue(step, channel.Address, channel.Value);
            }

            QueueStepSave(step.Id, channels);
        }

        if (Output == DmxDeskOutput.Live)
        {
            Result<bool> result = await desk.SetChannelsAsync(id, channels);

            if (!result.IsSuccess)
            {
                showError($"Couldn't send to the lights: {result.Error}");
            }
        }
        else if (!IsEditingStep)
        {
            HasUnsentChanges = true;
        }

        Touch();
    }

    /// <summary>
    /// Switches Live/Blind. Going Blind keeps what's on the desk as the starting buffer. Going Live
    /// with unsent changes needs <paramref name="sendChanges"/>: true sends them, false throws them
    /// away and shows the lights as they are.
    /// </summary>
    public async Task SetOutputAsync(DmxDeskOutput output, bool sendChanges = false)
    {
        if (output == Output)
        {
            return;
        }

        Output = output;

        if (output == DmxDeskOutput.Live)
        {
            if (IsEditingStep || (HasUnsentChanges && sendChanges))
            {
                // Editing a step: Live means preview the step on the lights, so they snap to it.
                await SendBufferAsync();
            }
            else
            {
                await ReloadLiveAsync();
            }

            HasUnsentChanges = false;
        }

        Touch();
    }

    /// <summary>Sends the whole desk to the lights in one batch.</summary>
    public async Task SendBufferAsync()
    {
        if (ClientId is not Guid id)
        {
            return;
        }

        List<DmxChannel> batch = Enumerable.Range(1, UniverseSize)
            .Select(a => new DmxChannel { Address = a, Value = Values[a] })
            .ToList();

        Result<bool> result = await desk.SetChannelsAsync(id, batch);

        if (!result.IsSuccess)
        {
            showError($"Couldn't send to the lights: {result.Error}");
            return;
        }

        HasUnsentChanges = false;
        Touch();
    }

    /// <summary>Blind: throw the desk's changes away and show the lights as they are now.</summary>
    public async Task DiscardBlindChangesAsync()
    {
        await ReloadLiveAsync();
        HasUnsentChanges = false;
        Touch();
    }

    public async Task SetGrandMasterAsync(byte level)
    {
        if (ClientId is not Guid id || IsLocked)
        {
            return;
        }

        OutputState = OutputState with { GrandMaster = level };
        Touch();

        Result<bool> result = await desk.SetGrandMasterAsync(id, level);

        if (!result.IsSuccess)
        {
            showError($"Couldn't set the master: {result.Error}");
        }
    }

    public async Task SetBlackoutAsync(bool isBlackout)
    {
        // Deliberately allowed while locked: blackout is the panic button.
        if (ClientId is not Guid id)
        {
            return;
        }

        OutputState = OutputState with { IsBlackout = isBlackout };
        Touch();

        Result<bool> result = await desk.SetBlackoutAsync(id, isBlackout);

        if (!result.IsSuccess)
        {
            showError($"Couldn't change blackout: {result.Error}");
        }
    }

    /// <summary>
    /// Binds the desk to a scene step: the faders show and save the step. Starts Blind, so editing
    /// doesn't change the lights until the operator chooses Live.
    /// </summary>
    public async Task EditStepAsync(DmxScene? scene, DmxSceneStep? step)
    {
        await FlushStepSavesAsync();

        EditingScene = step == null ? null : scene;
        EditingStep = step;

        if (step != null)
        {
            byte[] values = new byte[UniverseSize + 1];

            foreach (DmxSceneStepChannelValue value in step.ChannelValues)
            {
                if (value.ChannelNumber is >= 1 and <= UniverseSize)
                {
                    values[value.ChannelNumber] = value.Value;
                }
            }

            ReplaceValues(values);
            Output = DmxDeskOutput.Blind;
        }
        else
        {
            Output = DmxDeskOutput.Live;
            await ReloadLiveAsync();
        }

        HasUnsentChanges = false;
        Touch();
    }

    private static void SyncStepValue(DmxSceneStep step, int address, byte value)
    {
        DmxSceneStepChannelValue? existing = step.ChannelValues.FirstOrDefault(c => c.ChannelNumber == address);

        if (existing != null)
        {
            existing.Value = value;
        }
        else
        {
            step.ChannelValues.Add(new DmxSceneStepChannelValue
            {
                SceneStepId = step.Id,
                ChannelNumber = address,
                Value = value,
                CreateByUserId = string.Empty
            });
        }
    }

    private void QueueStepSave(Guid stepId, IReadOnlyList<DmxChannel> channels)
    {
        if (!_pendingStepSaves.TryGetValue(stepId, out Dictionary<int, byte>? pending))
        {
            pending = [];
            _pendingStepSaves[stepId] = pending;
        }

        foreach (DmxChannel channel in channels)
        {
            pending[channel.Address] = channel.Value;
        }

        _stepSaveDebounce?.Cancel();
        _stepSaveDebounce?.Dispose();
        _stepSaveDebounce = new CancellationTokenSource();

        _ = DebouncedFlushAsync(_stepSaveDebounce.Token);
    }

    private async Task DebouncedFlushAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(StepSaveDebounceMs, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        await dispatch(FlushStepSavesAsync);
    }

    /// <summary>Saves every pending step edit now (also done on leaving a step and on dispose).</summary>
    public async Task FlushStepSavesAsync()
    {
        if (_pendingStepSaves.Count == 0)
        {
            return;
        }

        List<KeyValuePair<Guid, Dictionary<int, byte>>> batches = [.. _pendingStepSaves];
        _pendingStepSaves.Clear();

        foreach ((Guid stepId, Dictionary<int, byte> values) in batches)
        {
            List<DmxChannel> batch = values.Select(v => new DmxChannel { Address = v.Key, Value = v.Value }).ToList();

            Result<bool> result = await sceneService.SaveSceneStepChannelValuesAsync(stepId, batch);

            if (!result.IsSuccess)
            {
                showError($"Failed to save {batch.Count} channel(s) of the step: {result.Error}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        _subscription = null;

        _stepSaveDebounce?.Cancel();
        _stepSaveDebounce?.Dispose();
        _stepSaveDebounce = null;

        try
        {
            await FlushStepSavesAsync();
        }
        catch (Exception)
        {
            // The circuit's scope may already be gone; nothing more can be saved then.
        }
    }
}

using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace Lanyard.Application.Services.Speech;

/// <summary>
/// Speaks with Piper voices through sherpa-onnx, inside the server process: nothing is sent
/// anywhere. A singleton, because loading a voice takes a couple of seconds and about 70 MB.
/// </summary>
/// <remarks>
/// Voices load the first time they're used. At most <see cref="SpeechOptions.MaxLoadedVoices"/>
/// stay loaded (the least recently used goes first), and one left idle for
/// <see cref="SpeechOptions.IdleUnloadMinutes"/> is unloaded, so a venue that never speaks costs
/// no memory at all. A loaded voice speaks one announcement at a time; sherpa-onnx doesn't say
/// its native object is safe to share across threads.
///
/// A voice is only ever disposed when nobody holds it: <see cref="LoadedVoice.Users"/> is counted
/// under <c>_lock</c> from the moment a request is handed the voice until it has finished with it,
/// and unloading skips any voice with users. One that's unloaded while held (it can't be, but if
/// that changes) is disposed by its last user.
/// </remarks>
public sealed class PiperSpeechEngine : ISpeechEngine, IDisposable
{
    private sealed class LoadedVoice(string key, OfflineTts tts)
    {
        public string Key { get; } = key;
        public OfflineTts Tts { get; } = tts;

        // Serialises speaking on this voice. Never disposed: a SemaphoreSlim holds no native handle
        // unless its wait handle is used, and disposing it is what made a waiting request throw.
        public SemaphoreSlim Gate { get; } = new(1, 1);

        // Guarded by _lock.
        public int Users { get; set; }
        public bool Unloaded { get; set; }
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    }

    private readonly SpeechOptions _options;
    private readonly ILogger<PiperSpeechEngine> _logger;
    private readonly Dictionary<string, LoadedVoice> _loaded = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    // Only one model loads at a time (each load is ~2 s of CPU and ~70 MB), but loading never
    // holds _lock, so voices that are already loaded carry on speaking meanwhile.
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly Timer _idleTimer;
    private bool _disposed;

    public PiperSpeechEngine(IOptions<SpeechOptions> options, ILogger<PiperSpeechEngine> logger)
    {
        _options = options.Value;
        _logger = logger;
        _idleTimer = new Timer(_ => UnloadIdleVoices(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public SpeechEngineKind Kind => SpeechEngineKind.Piper;

    /// <summary>The folder the voices were found in, or null when none are installed.</summary>
    public string? VoicesPath => ResolveVoicesPath(_options.VoicesPath);

    public bool IsInstalled(SpeechVoice voice) => FindModel(voice) is not null;

    public async Task<Result<SpeechSamples>> SynthesiseAsync(string text, SpeechVoice voice, double speed, CancellationToken ct)
    {
        if (voice.Engine != SpeechEngineKind.Piper)
        {
            return Result<SpeechSamples>.Fail("That voice isn't a Piper voice.");
        }

        LoadedVoice? held = null;

        try
        {
            held = await AcquireAsync(voice, ct);

            if (held is null)
            {
                return Result<SpeechSamples>.Fail($"The voice {voice.Name} isn't installed on this server.");
            }

            await held.Gate.WaitAsync(ct);

            try
            {
                // Native and CPU-bound: off the request thread. It can't be cancelled part way, but
                // a sentence takes about a second. Samples is a managed copy; the native buffer
                // behind it is freed by OfflineTtsGeneratedAudio's own finaliser.
                LoadedVoice speaking = held;
                SpeechSamples samples = await Task.Run(() =>
                {
                    OfflineTtsGeneratedAudio audio = speaking.Tts.Generate(text, (float)speed, voice.SpeakerId);
                    return new SpeechSamples(audio.Samples, audio.SampleRate);
                }, ct);

                return Result<SpeechSamples>.Ok(samples);
            }
            finally
            {
                held.Gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Piper failed to speak with voice {VoiceId}", voice.Id);
            return Result<SpeechSamples>.Fail($"The voice couldn't speak that: {ex.Message}");
        }
        finally
        {
            if (held is not null)
            {
                Release(held);
            }
        }
    }

    // Hands back the voice with one more user counted, loading it first if need be.
    private async Task<LoadedVoice?> AcquireAsync(SpeechVoice voice, CancellationToken ct)
    {
        if (TryAcquireLoaded(voice.ModelFolder) is LoadedVoice loaded)
        {
            return loaded;
        }

        await _loadGate.WaitAsync(ct);

        try
        {
            // Someone else may have loaded it while this waited.
            if (TryAcquireLoaded(voice.ModelFolder) is LoadedVoice raced)
            {
                return raced;
            }

            (string Model, string Tokens, string DataDir)? files = FindModel(voice);

            if (files is null)
            {
                return null;
            }

            OfflineTtsConfig config = new();
            config.Model.Vits.Model = files.Value.Model;
            config.Model.Vits.Tokens = files.Value.Tokens;
            config.Model.Vits.DataDir = files.Value.DataDir;
            config.Model.NumThreads = Math.Max(1, _options.NumThreads);
            config.Model.Provider = "cpu";

            OfflineTts tts = await Task.Run(() => new OfflineTts(config), ct);
            LoadedVoice fresh = new(voice.ModelFolder, tts) { Users = 1 };

            lock (_lock)
            {
                if (_disposed)
                {
                    tts.Dispose();
                    throw new ObjectDisposedException(nameof(PiperSpeechEngine));
                }

                MakeRoom(Math.Max(1, _options.MaxLoadedVoices) - 1);
                _loaded[voice.ModelFolder] = fresh;
            }

            _logger.LogInformation("Loaded speech model {ModelFolder}", voice.ModelFolder);

            return fresh;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private LoadedVoice? TryAcquireLoaded(string key)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_loaded.TryGetValue(key, out LoadedVoice? existing))
            {
                return null;
            }

            existing.Users++;
            existing.LastUsedUtc = DateTime.UtcNow;
            return existing;
        }
    }

    private void Release(LoadedVoice voice)
    {
        lock (_lock)
        {
            voice.Users--;
            voice.LastUsedUtc = DateTime.UtcNow;

            if (voice.Unloaded && voice.Users == 0)
            {
                voice.Tts.Dispose();
            }
        }
    }

    // Unloads the least recently used voices until at most `keep` remain. Called under _lock. A
    // voice someone holds stays, so for a moment one more than the limit can be loaded.
    private void MakeRoom(int keep)
    {
        foreach (LoadedVoice candidate in _loaded.Values.OrderBy(x => x.LastUsedUtc).ToList())
        {
            if (_loaded.Count <= keep)
            {
                return;
            }

            Unload(candidate);
        }
    }

    private void UnloadIdleVoices()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow.AddMinutes(-Math.Max(1, _options.IdleUnloadMinutes));

            foreach (LoadedVoice idle in _loaded.Values.Where(x => x.LastUsedUtc < cutoff).ToList())
            {
                Unload(idle);
            }
        }
    }

    // Called under _lock.
    private void Unload(LoadedVoice voice)
    {
        if (voice.Users > 0)
        {
            return;
        }

        _loaded.Remove(voice.Key);
        voice.Unloaded = true;
        voice.Tts.Dispose();

        _logger.LogInformation("Unloaded speech model {ModelFolder}", voice.Key);
    }

    private (string Model, string Tokens, string DataDir)? FindModel(SpeechVoice voice)
    {
        string? root = VoicesPath;

        if (root is null)
        {
            return null;
        }

        string folder = Path.Combine(root, voice.ModelFolder);
        string tokens = Path.Combine(folder, "tokens.txt");
        string? model = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.onnx").FirstOrDefault() : null;

        // Each voice can carry its own espeak-ng-data; the Docker image keeps one shared copy.
        string dataDir = Directory.Exists(Path.Combine(folder, "espeak-ng-data"))
            ? Path.Combine(folder, "espeak-ng-data")
            : Path.Combine(root, "espeak-ng-data");

        return model is not null && File.Exists(tokens) && Directory.Exists(dataDir) ? (model, tokens, dataDir) : null;
    }

    public static string? ResolveVoicesPath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Directory.Exists(configured) ? configured : null;
        }

        string beside = Path.Combine(AppContext.BaseDirectory, "voices");
        if (Directory.Exists(beside))
        {
            return beside;
        }

        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lanyard", "Voices");
        return Directory.Exists(local) ? local : null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _idleTimer.Dispose();

            // A voice still in use is disposed by its last user (Release).
            foreach (LoadedVoice voice in _loaded.Values)
            {
                voice.Unloaded = true;

                if (voice.Users == 0)
                {
                    voice.Tts.Dispose();
                }
            }

            _loaded.Clear();
        }
    }
}

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
/// </remarks>
public sealed class PiperSpeechEngine : ISpeechEngine, IDisposable
{
    private sealed class LoadedVoice(OfflineTts tts) : IDisposable
    {
        public OfflineTts Tts { get; } = tts;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

        public void Dispose()
        {
            Tts.Dispose();
            Gate.Dispose();
        }
    }

    private readonly SpeechOptions _options;
    private readonly ILogger<PiperSpeechEngine> _logger;
    private readonly Dictionary<string, LoadedVoice> _loaded = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
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
        try
        {
            if (voice.Engine != SpeechEngineKind.Piper)
            {
                return Result<SpeechSamples>.Fail("That voice isn't a Piper voice.");
            }

            LoadedVoice? loaded = await Task.Run(() => GetOrLoad(voice), ct);

            if (loaded is null)
            {
                return Result<SpeechSamples>.Fail($"The voice {voice.Name} isn't installed on this server.");
            }

            await loaded.Gate.WaitAsync(ct);

            try
            {
                loaded.LastUsedUtc = DateTime.UtcNow;

                // Native and CPU-bound: off the request thread. It can't be cancelled part way, but
                // a sentence takes about a second.
                OfflineTtsGeneratedAudio audio = await Task.Run(() => loaded.Tts.Generate(text, (float)speed, voice.SpeakerId), ct);

                return Result<SpeechSamples>.Ok(new SpeechSamples(audio.Samples, audio.SampleRate));
            }
            finally
            {
                loaded.LastUsedUtc = DateTime.UtcNow;
                loaded.Gate.Release();
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
    }

    private LoadedVoice? GetOrLoad(SpeechVoice voice)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_loaded.TryGetValue(voice.ModelFolder, out LoadedVoice? existing))
            {
                existing.LastUsedUtc = DateTime.UtcNow;
                return existing;
            }

            (string Model, string Tokens, string DataDir)? files = FindModel(voice);

            if (files is null)
            {
                return null;
            }

            MakeRoom(Math.Max(1, _options.MaxLoadedVoices) - 1);

            OfflineTtsConfig config = new();
            config.Model.Vits.Model = files.Value.Model;
            config.Model.Vits.Tokens = files.Value.Tokens;
            config.Model.Vits.DataDir = files.Value.DataDir;
            config.Model.NumThreads = Math.Max(1, _options.NumThreads);
            config.Model.Provider = "cpu";

            LoadedVoice loaded = new(new OfflineTts(config));
            _loaded[voice.ModelFolder] = loaded;

            _logger.LogInformation("Loaded speech model {ModelFolder}", voice.ModelFolder);

            return loaded;
        }
    }

    // Unloads the least recently used voices until at most `keep` remain. Called under _lock. A
    // voice that's busy speaking stays, so for a moment one more than the limit can be loaded.
    private void MakeRoom(int keep)
    {
        foreach (KeyValuePair<string, LoadedVoice> candidate in _loaded.OrderBy(x => x.Value.LastUsedUtc).ToList())
        {
            if (_loaded.Count <= keep)
            {
                return;
            }

            Unload(candidate.Key, candidate.Value);
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

            foreach (KeyValuePair<string, LoadedVoice> idle in _loaded.Where(x => x.Value.LastUsedUtc < cutoff).ToList())
            {
                Unload(idle.Key, idle.Value);
            }
        }
    }

    // Called under _lock. A voice speaking right now is left alone; it'll be idle soon enough.
    private void Unload(string key, LoadedVoice voice)
    {
        if (!voice.Gate.Wait(0))
        {
            voice.LastUsedUtc = DateTime.UtcNow;
            return;
        }

        _loaded.Remove(key);
        voice.Gate.Release();
        voice.Dispose();

        _logger.LogInformation("Unloaded speech model {ModelFolder}", key);
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

            foreach (LoadedVoice voice in _loaded.Values)
            {
                voice.Dispose();
            }

            _loaded.Clear();
        }
    }
}

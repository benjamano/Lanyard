using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Lanyard.Client.Controllers;
using Lanyard.Client.PacketSniffing;
using Lanyard.Client.Security;
using Lanyard.Client.SignalR;
using Lanyard.Shared.DTO;
using Lanyard.Shared.LaserGame.Capture;
using Lanyard.Shared.LaserGame.Pipeline;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.GameRecordings;

public interface IPacketCaptureCoordinator
{
    PacketCaptureStateDTO GetState();

    Task<PacketCaptureCommandResultDTO> StartRecordingAsync(StartPacketRecordingRequestDTO request);
    Task<PacketCaptureCommandResultDTO> StopRecordingAsync();

    /// <summary>Downloads the recording from File Manager with the kiosk's credential, then replays it.</summary>
    Task<PacketCaptureCommandResultDTO> StartReplayAsync(StartPacketReplayRequestDTO request, CancellationToken cancellationToken);

    /// <summary>Replays a file on this machine, for the LANYARD_CLIENT_REPLAY_FILE developer flag.</summary>
    Task<PacketCaptureCommandResultDTO> StartReplayFromFileAsync(string path, double speed, CancellationToken cancellationToken);

    Task<PacketCaptureCommandResultDTO> StopReplayAsync();

    /// <summary>Starts listening for games and connections, and uploads anything left from before.</summary>
    void Start();
}

/// <summary>
/// The kiosk end of recording and replaying laser games. It does one thing at a time: recording
/// and replaying are never on together, so a replayed game is never recorded as a real one.
/// Finished recordings wait in <see cref="CaptureDirectory"/> until the server takes them, and
/// are retried on every reconnect for up to <see cref="KeepUnsentFor"/>.
/// </summary>
public sealed class PacketCaptureCoordinator(
    IPacketSniffer sniffer,
    ILaserPacketPipeline pipeline,
    IGameStateService gameState,
    ISignalRClient signalRClient,
    ILogger<PacketCaptureCoordinator> logger) : IPacketCaptureCoordinator
{
    public static readonly TimeSpan KeepUnsentFor = TimeSpan.FromDays(7);

    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(2);

    public static string CaptureDirectory { get; } = Path.Combine(ClientIdentity.DataDirectory, "packet-captures");
    private static string ReplayCacheDirectory { get; } = Path.Combine(CaptureDirectory, "replay-cache");

    // Commands arrive from the hub and the startup flag; one at a time keeps the mode honest.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _uploadGate = new(1, 1);
    private readonly object _stateLock = new();

    private PacketCaptureMode _mode = PacketCaptureMode.Idle;
    private DateTime? _startedUtc;
    private string? _name;
    private double _speed;
    private long _replayPlayed;
    private int _replayTotal;
    private string? _lastMessage;
    private bool _lastMessageIsError;
    private int _pendingUploads;
    private DateTime _lastProgressReport = DateTime.MinValue;

    private LaserCaptureRecorder? _recorder;
    private string? _recordingPath;

    private CancellationTokenSource? _replayCancel;
    private Task? _replayTask;

    private int _started;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        // Only live games count towards a recording; a replay never runs while one is going.
        gameState.GameStarted += () =>
        {
            if (gameState.Source == LaserPacketSource.Live)
            {
                Volatile.Read(ref _recorder)?.OnGameStarted();
            }
        };

        gameState.GameEnded += () =>
        {
            if (gameState.Source == LaserPacketSource.Live)
            {
                Volatile.Read(ref _recorder)?.OnGameEnded();
            }
        };

        pipeline.LiveGameTookOver += OnLiveGameTookOverWhileIdle;

        signalRClient.ConnectionStateChanged += connected =>
        {
            if (connected)
            {
                _ = Task.Run(async () =>
                {
                    await UploadPendingAsync();
                    await ReportStateAsync(force: true);
                });
            }
        };

        _ = Task.Run(async () =>
        {
            DeleteExpiredFiles();
            await UploadPendingAsync();
        });
    }

    public PacketCaptureStateDTO GetState()
    {
        lock (_stateLock)
        {
            LaserCaptureRecorder? recorder = _recorder;

            return new PacketCaptureStateDTO
            {
                Mode = _mode,
                StartedUtc = _startedUtc,
                Packets = _mode == PacketCaptureMode.Recording ? recorder?.Recorded ?? 0 : Interlocked.Read(ref _replayPlayed),
                Dropped = _mode == PacketCaptureMode.Recording ? recorder?.Dropped ?? 0 : 0,
                ProgressPercent = _replayTotal == 0 ? 0 : (int)(Interlocked.Read(ref _replayPlayed) * 100 / _replayTotal),
                Speed = _speed,
                Name = _name,
                PendingUploads = _pendingUploads,
                LastMessage = _lastMessage,
                LastMessageIsError = _lastMessageIsError,
                UpdatedUtc = DateTime.UtcNow,
            };
        }
    }

    public async Task<PacketCaptureCommandResultDTO> StartRecordingAsync(StartPacketRecordingRequestDTO request)
    {
        await _gate.WaitAsync();

        try
        {
            switch (CurrentMode())
            {
                case PacketCaptureMode.Recording:
                    return Refuse("This kiosk is already recording.");
                case PacketCaptureMode.Downloading:
                case PacketCaptureMode.Replaying:
                    return Refuse("A replay is running on this kiosk. Stop it before recording.");
                case PacketCaptureMode.ReplayFinished:
                    // The replay's final scores are still up; recording follows the arena again.
                    pipeline.SwitchSource(LaserPacketSource.Live);
                    break;
            }

            if (!sniffer.IsCapturing)
            {
                return Refuse("This kiosk isn't capturing game packets, so there's nothing to record. Check its zone scoreboard settings and that Npcap is installed.");
            }

            Directory.CreateDirectory(CaptureDirectory);

            string path = Path.Combine(CaptureDirectory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24] + LaserCaptureFormat.FileExtension);
            string? note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();

            if (note is { Length: > StartPacketRecordingRequestDTO.MaxNoteLength })
            {
                note = note[..StartPacketRecordingRequestDTO.MaxNoteLength];
            }

            FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: true);

            LaserCaptureRecorder recorder = new(file, BuildHeader(note, request.Mode), new LaserCaptureRecordingOptions
            {
                Mode = request.Mode,
                MaxDuration = TimeSpan.FromMinutes(PacketCaptureRules.ClampMaxMinutes(request.MaxMinutes)),
            });

            recorder.Start();
            sniffer.AttachRecorder(recorder);

            lock (_stateLock)
            {
                _recorder = recorder;
                _recordingPath = path;
                _mode = PacketCaptureMode.Recording;
                _startedUtc = recorder.StartedUtc;
                _name = note;
                _speed = 0;
                SetMessage(null, false);
            }

            logger.LogInformation("Recording laser game packets to {Path} ({Mode}, up to {Minutes} min).", path, request.Mode, PacketCaptureRules.ClampMaxMinutes(request.MaxMinutes));

            _ = recorder.Completion.ContinueWith(task => OnRecordingFinishedAsync(recorder, path, task.Result), TaskScheduler.Default).Unwrap();

            await ReportStateAsync(force: true);

            return PacketCaptureCommandResultDTO.Ok(GetState());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't start recording laser game packets.");

            return Refuse($"Couldn't start recording: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PacketCaptureCommandResultDTO> StopRecordingAsync()
    {
        LaserCaptureRecorder? recorder;

        lock (_stateLock)
        {
            recorder = _mode == PacketCaptureMode.Recording ? _recorder : null;
        }

        if (recorder == null)
        {
            return Refuse("This kiosk isn't recording.");
        }

        await recorder.StopAsync();

        // OnRecordingFinishedAsync runs as a continuation; give it the chance to settle the state.
        for (int i = 0; i < 50 && CurrentMode() == PacketCaptureMode.Recording; i++)
        {
            await Task.Delay(20);
        }

        return PacketCaptureCommandResultDTO.Ok(GetState());
    }

    private async Task OnRecordingFinishedAsync(LaserCaptureRecorder recorder, string path, LaserCaptureRecordingResult result)
    {
        sniffer.AttachRecorder(null);
        await recorder.DisposeAsync();

        string message;
        bool isError = false;

        if (result.Reason == LaserCaptureStopReason.Failed)
        {
            message = $"Recording stopped because the file couldn't be written: {result.Error}";
            isError = true;
        }
        else if (result.Packets == 0)
        {
            message = "Nothing was recorded: no game packets arrived.";
            TryDelete(path);
        }
        else
        {
            string why = result.Reason switch
            {
                LaserCaptureStopReason.GameFinished => "the game finished",
                LaserCaptureStopReason.MaxDuration => "it reached its time limit",
                LaserCaptureStopReason.MaxSize => "it reached its size limit",
                _ => "it was stopped",
            };

            message = $"Recorded {result.Packets:N0} packets ({result.Games} whole {(result.Games == 1 ? "game" : "games")}); stopped because {why}.";

            if (result.Dropped > 0)
            {
                message += $" {result.Dropped:N0} packets were missed.";
            }
        }

        logger.LogInformation("Laser game recording {Path} finished ({Reason}): {Packets} packets, {Dropped} dropped, {Games} games.", path, result.Reason, result.Packets, result.Dropped, result.Games);

        lock (_stateLock)
        {
            if (ReferenceEquals(_recorder, recorder))
            {
                _recorder = null;
                _recordingPath = null;
                _mode = PacketCaptureMode.Idle;
                _startedUtc = null;
                _name = null;
            }

            SetMessage(message, isError);
        }

        await ReportStateAsync(force: true);
        await UploadPendingAsync();
    }

    public async Task<PacketCaptureCommandResultDTO> StartReplayAsync(StartPacketReplayRequestDTO request, CancellationToken cancellationToken)
    {
        if (!PacketCaptureRules.IsAllowedSpeed(request.Speed))
        {
            return Refuse($"{request.Speed}x isn't a replay speed this kiosk supports.");
        }

        if (request.FileId == Guid.Empty || request.RecordingId == Guid.Empty)
        {
            return Refuse("No recording was chosen.");
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (RefuseIfBusy() is PacketCaptureCommandResultDTO busy)
            {
                return busy;
            }

            SetMode(PacketCaptureMode.Downloading, request.Name, request.Speed);
            await ReportStateAsync(force: true);

            LaserCapture capture;

            try
            {
                string path = await DownloadAsync(request.RecordingId, request.FileId, cancellationToken);

                await using FileStream file = File.OpenRead(path);
                capture = await LaserCaptureReader.ReadAsync(file, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Couldn't fetch recording {RecordingId} (file {FileId}) to replay.", request.RecordingId, request.FileId);

                SetMode(PacketCaptureMode.Idle, null, 0);
                return Refuse(ex is LaserCaptureFormatException ? $"That recording can't be read: {ex.Message}" : $"Couldn't download the recording: {ex.Message}");
            }

            return await BeginReplayAsync(capture, request.Speed, request.Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PacketCaptureCommandResultDTO> StartReplayFromFileAsync(string path, double speed, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (RefuseIfBusy() is PacketCaptureCommandResultDTO busy)
            {
                return busy;
            }

            LaserCapture capture;

            try
            {
                await using FileStream file = File.OpenRead(path);
                capture = await LaserCaptureReader.ReadAsync(file, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LaserCaptureFormatException)
            {
                return Refuse($"Couldn't read {path}: {ex.Message}");
            }

            return await BeginReplayAsync(capture, speed, capture.Header.Note ?? Path.GetFileName(path));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PacketCaptureCommandResultDTO> BeginReplayAsync(LaserCapture capture, double speed, string? name)
    {
        LaserReplayRunner runner = new(pipeline);

        if (!runner.TryBegin())
        {
            SetMode(PacketCaptureMode.Idle, null, 0);
            return Refuse("A real game is in progress on this arena. Try again once it has ended.");
        }

        CancellationTokenSource cancel = new();

        lock (_stateLock)
        {
            _mode = PacketCaptureMode.Replaying;
            _startedUtc = DateTime.UtcNow;
            _name = name;
            _speed = speed;
            _replayTotal = capture.Packets.Count;
            Interlocked.Exchange(ref _replayPlayed, 0);
            _replayCancel = cancel;
            SetMessage(null, false);
        }

        logger.LogInformation("Replaying {Name}: {Packets} packets at {Speed}x.", name, capture.Packets.Count, speed);

        Progress<LaserReplayProgress> progress = new(p =>
        {
            Interlocked.Exchange(ref _replayPlayed, p.PacketsPlayed);
            _ = ReportStateAsync(force: false);
        });

        _replayTask = Task.Run(async () =>
        {
            LaserReplayOutcome outcome;

            try
            {
                outcome = await runner.PlayAsync(capture, speed, progress, cancel.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The replay of {Name} failed.", name);
                pipeline.SwitchSource(LaserPacketSource.Live);
                FinishReplay(PacketCaptureMode.Idle, $"The replay failed: {ex.Message}", true);
                await ReportStateAsync(force: true);
                return;
            }

            logger.LogInformation("Replay of {Name} ended ({End}) after {Played} of {Total} packets.", name, outcome.End, outcome.PacketsPlayed, outcome.TotalPackets);

            switch (outcome.End)
            {
                case LaserReplayEnd.Completed:
                    FinishReplay(PacketCaptureMode.ReplayFinished, "Replay finished. Its final scores stay up until the next game starts or you stop the replay.", false);
                    break;
                case LaserReplayEnd.TakenOver:
                    FinishReplay(PacketCaptureMode.Idle, "A real game started on the arena, so the replay stopped.", false);
                    break;
                default:
                    FinishReplay(PacketCaptureMode.Idle, "Replay stopped.", false);
                    break;
            }

            await ReportStateAsync(force: true);
        });

        await ReportStateAsync(force: true);

        return PacketCaptureCommandResultDTO.Ok(GetState());
    }

    public async Task<PacketCaptureCommandResultDTO> StopReplayAsync()
    {
        await _gate.WaitAsync();

        try
        {
            CancellationTokenSource? cancel;
            Task? running;
            PacketCaptureMode mode;

            lock (_stateLock)
            {
                mode = _mode;
                cancel = _replayCancel;
                running = _replayTask;
            }

            if (mode is not (PacketCaptureMode.Replaying or PacketCaptureMode.ReplayFinished))
            {
                return Refuse("No replay is running on this kiosk.");
            }

            if (mode == PacketCaptureMode.Replaying && cancel != null)
            {
                cancel.Cancel();

                if (running != null)
                {
                    await running;
                }
            }

            // Back to the arena: the replay's game and scores leave the screen.
            pipeline.SwitchSource(LaserPacketSource.Live);
            FinishReplay(PacketCaptureMode.Idle, "Replay stopped.", false);

            await ReportStateAsync(force: true);

            return PacketCaptureCommandResultDTO.Ok(GetState());
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnLiveGameTookOverWhileIdle()
    {
        // While playing, the replay runner sees the takeover itself. Afterwards only the final
        // scores were on screen, and the live game has just replaced them.
        if (CurrentMode() == PacketCaptureMode.ReplayFinished)
        {
            FinishReplay(PacketCaptureMode.Idle, "A real game started, so the replay's scores were cleared.", false);
            _ = ReportStateAsync(force: true);
        }
    }

    private void FinishReplay(PacketCaptureMode mode, string message, bool isError)
    {
        lock (_stateLock)
        {
            _mode = mode;

            if (mode == PacketCaptureMode.Idle)
            {
                _startedUtc = null;
                _name = null;
                _speed = 0;
                _replayTotal = 0;
                Interlocked.Exchange(ref _replayPlayed, 0);
            }

            _replayCancel?.Dispose();
            _replayCancel = null;

            SetMessage(message, isError);
        }
    }

    private PacketCaptureCommandResultDTO? RefuseIfBusy() => CurrentMode() switch
    {
        PacketCaptureMode.Recording => Refuse("This kiosk is recording. Stop the recording before replaying."),
        PacketCaptureMode.Downloading or PacketCaptureMode.Replaying => Refuse("A replay is already running on this kiosk."),
        _ => null,
    };

    private PacketCaptureCommandResultDTO Refuse(string error) => PacketCaptureCommandResultDTO.Refused(error, GetState());

    private PacketCaptureMode CurrentMode()
    {
        lock (_stateLock)
        {
            return _mode;
        }
    }

    private void SetMode(PacketCaptureMode mode, string? name, double speed)
    {
        lock (_stateLock)
        {
            _mode = mode;
            _name = name;
            _speed = speed;
            _startedUtc = mode == PacketCaptureMode.Idle ? null : DateTime.UtcNow;
        }
    }

    // Caller holds _stateLock.
    private void SetMessage(string? message, bool isError)
    {
        _lastMessage = message;
        _lastMessageIsError = isError;
    }

    private LaserCaptureHeader BuildHeader(string? note, LaserCaptureStopMode mode)
    {
        Lanyard.Infrastructure.DTO.ZoneScoreboard.ZoneScoreboardSettingsDTO? settings = sniffer.CurrentSettings;

        return new LaserCaptureHeader
        {
            RecordedAtUtc = DateTime.UtcNow,
            ClientVersion = ClientDiagnosticsController.GetClientVersion(),
            Note = note,
            Mode = mode.ToString(),
            Settings = settings == null ? null : new LaserCaptureSettings
            {
                SourceIp = settings.SourceIp,
                DestinationIp = settings.DestinationIp,
                UseCaptureFilter = settings.UseCaptureFilter,
                PromiscuousCapture = settings.PromiscuousCapture,
                CaptureFilterApplied = sniffer.CaptureFilter,
                ZoneScoreboardVersion = settings.ZoneScoreboardVersion.ToString(),
            },
        };
    }

    private async Task ReportStateAsync(bool force)
    {
        if (!force)
        {
            DateTime now = DateTime.UtcNow;

            lock (_stateLock)
            {
                if (now - _lastProgressReport < ProgressReportInterval)
                {
                    return;
                }

                _lastProgressReport = now;
            }
        }

        try
        {
            await signalRClient.ReportPacketCaptureStateAsync(GetState());
        }
        catch (Exception ex)
        {
            // The next change reports again; a dropped report only delays the Clients page.
            logger.LogDebug(ex, "Couldn't report the packet capture state to the server.");
        }
    }

    private async Task<string> DownloadAsync(Guid recordingId, Guid fileId, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ReplayCacheDirectory);

        // The name comes from the server's ids, never from a path it sends.
        string path = Path.Combine(ReplayCacheDirectory, recordingId.ToString("N") + LaserCaptureFormat.FileExtension);

        if (File.Exists(path))
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return path;
        }

        using HttpClient http = CreateHttpClient();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HttpTimeout);

        using HttpRequestMessage request = new(HttpMethod.Get, $"api/files/download/{fileId}");
        KioskCredentials.ApplyTo(request);

        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > LaserCaptureReader.MaxFileBytes)
        {
            throw new LaserCaptureFormatException("The file is too big to be a laser game recording.");
        }

        string partial = path + ".part";

        await using (FileStream file = new(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await response.Content.CopyToAsync(file, timeout.Token);
        }

        File.Move(partial, path, overwrite: true);

        return path;
    }

    private async Task UploadPendingAsync()
    {
        if (!await _uploadGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (!Directory.Exists(CaptureDirectory) || !Guid.TryParse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID"), out Guid clientId))
            {
                return;
            }

            string? recording;

            lock (_stateLock)
            {
                recording = _recordingPath;
            }

            List<string> pending = Directory.GetFiles(CaptureDirectory, "*" + LaserCaptureFormat.FileExtension)
                .Where(path => !string.Equals(path, recording, StringComparison.OrdinalIgnoreCase))
                .Order()
                .ToList();

            SetPending(pending.Count);

            if (pending.Count == 0 || !signalRClient.IsConnected)
            {
                return;
            }

            using HttpClient http = CreateHttpClient();

            foreach (string path in pending)
            {
                bool done = await TryUploadAsync(http, clientId, path);

                if (!done)
                {
                    // Try again on the next reconnect; the server is likely unreachable right now.
                    break;
                }

                SetPending(Math.Max(0, CurrentPending() - 1));
            }

            await ReportStateAsync(force: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Uploading finished laser game recordings failed; trying again on the next reconnect.");
        }
        finally
        {
            _uploadGate.Release();
        }
    }

    /// <returns>True when the file is dealt with (uploaded, or rejected for good) and the next can go.</returns>
    private async Task<bool> TryUploadAsync(HttpClient http, Guid clientId, string path)
    {
        using CancellationTokenSource timeout = new(HttpTimeout);

        await using FileStream file = File.OpenRead(path);

        using MultipartFormDataContent form = new();
        using StreamContent content = new(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
        form.Add(content, PacketCaptureRoutes.FileField, Path.GetFileName(path));

        using HttpRequestMessage request = new(HttpMethod.Post, PacketCaptureRoutes.Upload(clientId)) { Content = form };
        KioskCredentials.ApplyTo(request);

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            logger.LogInformation("Couldn't upload recording {Path} yet: {Error}", path, ex.Message);
            return false;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                file.Close();
                TryDelete(path);

                logger.LogInformation("Uploaded laser game recording {Path} to the server.", path);
                SetMessageAndKeepMode("Recording uploaded to File Manager.", false);
                return true;
            }

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity)
            {
                // The server will never take this file; keeping it would block every later one.
                string reason = await response.Content.ReadAsStringAsync();
                file.Close();
                TryDelete(path);

                logger.LogWarning("The server rejected recording {Path} ({Status}): {Reason}", path, (int)response.StatusCode, reason);
                SetMessageAndKeepMode($"The server couldn't take a recording ({(int)response.StatusCode}), so it was discarded.", true);
                return true;
            }

            // 401/403/404/5xx: a server without the endpoint yet, or one that's struggling. Keep it.
            logger.LogInformation("The server didn't take recording {Path} yet ({Status}); keeping it for later.", path, (int)response.StatusCode);
            return false;
        }
    }

    private void DeleteExpiredFiles()
    {
        try
        {
            if (!Directory.Exists(CaptureDirectory))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow - KeepUnsentFor;

            foreach (string path in Directory.EnumerateFiles(CaptureDirectory, "*", SearchOption.AllDirectories))
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    logger.LogInformation("Deleting old laser game recording {Path}.", path);
                    TryDelete(path);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't clear out old laser game recordings.");
        }
    }

    private void SetPending(int count)
    {
        lock (_stateLock)
        {
            _pendingUploads = count;
        }
    }

    private int CurrentPending()
    {
        lock (_stateLock)
        {
            return _pendingUploads;
        }
    }

    private void SetMessageAndKeepMode(string message, bool isError)
    {
        lock (_stateLock)
        {
            SetMessage(message, isError);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't delete {Path}.", path);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClientHandler handler = new();

        if (bool.TryParse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ALLOW_INSECURE_SSL"), out bool insecure) && insecure)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("LANYARD_SERVER_URL")!.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}

using System.Threading.Channels;

namespace Lanyard.Shared.Music;

/// <summary>
/// Runs the kiosk's music commands one at a time, in the order they arrived, off the SignalR
/// connection's thread.
/// </summary>
/// <remarks>
/// <para>
/// SignalR runs a connection's handlers one after another, so a handler that awaits a song
/// download used to hold up every other message to the kiosk - pausing, stopping an audio clip,
/// DMX - until the download finished. Handlers now just add the command here and return.
/// </para>
/// <para>
/// Order still matters (the server sends "load this", then "play"), so commands run strictly one
/// at a time. A new load makes any older load pointless: the one running is cancelled (which
/// abandons its download wait) and any still waiting are skipped.
/// </para>
/// </remarks>
public sealed class MusicCommandQueue : IAsyncDisposable
{
    private sealed record Command(string Name, Func<CancellationToken, Task> Work, long? LoadGeneration);

    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Action<string, Exception>? _onError;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private readonly object _lock = new();

    private long _latestLoad;
    private CancellationTokenSource? _runningLoad;

    public MusicCommandQueue(Action<string, Exception>? onError = null)
    {
        _onError = onError;
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Raised (on the worker) when a load is skipped because a newer one replaced it.</summary>
    public event Action<string>? Superseded;

    /// <summary>Queues a command that runs after everything queued before it.</summary>
    public void Enqueue(string name, Func<CancellationToken, Task> work) =>
        _commands.Writer.TryWrite(new Command(name, work, null));

    /// <summary>
    /// Queues a load. Cancels a load that's running now and skips any queued earlier, since only
    /// the newest load decides what ends up playing.
    /// </summary>
    public void EnqueueLoad(string name, Func<CancellationToken, Task> work)
    {
        long generation;

        lock (_lock)
        {
            generation = ++_latestLoad;
            _runningLoad?.Cancel();
        }

        _commands.Writer.TryWrite(new Command(name, work, generation));
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (Command command in _commands.Reader.ReadAllAsync(_shutdown.Token))
            {
                CancellationTokenSource? loadCts = null;
                CancellationToken token = _shutdown.Token;

                if (command.LoadGeneration is long generation)
                {
                    lock (_lock)
                    {
                        if (generation != _latestLoad)
                        {
                            Superseded?.Invoke(command.Name);
                            continue;
                        }

                        loadCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                        _runningLoad = loadCts;
                        token = loadCts.Token;
                    }
                }

                try
                {
                    await command.Work(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    if (_shutdown.IsCancellationRequested)
                    {
                        return;
                    }

                    Superseded?.Invoke(command.Name);
                }
                catch (Exception ex)
                {
                    // One failed command mustn't stop the ones after it.
                    _onError?.Invoke(command.Name, ex);
                }
                finally
                {
                    if (loadCts is not null)
                    {
                        lock (_lock)
                        {
                            if (ReferenceEquals(_runningLoad, loadCts))
                            {
                                _runningLoad = null;
                            }
                        }

                        loadCts.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    /// <summary>Waits for everything queued so far to run. For tests and shutdown.</summary>
    public Task DrainAsync()
    {
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue("drain", _ =>
        {
            done.TrySetResult();
            return Task.CompletedTask;
        });

        return done.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _commands.Writer.TryComplete();
        _shutdown.Cancel();

        try
        {
            await _worker;
        }
        catch (OperationCanceledException)
        {
        }

        _shutdown.Dispose();
    }
}

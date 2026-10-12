using Lanyard.Shared.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.Diagnostics;

/// <summary>
/// Copies every Information-or-higher log line into the <see cref="LogLineBuffer"/> that answers
/// remote diagnostics log-tail requests. Lines are redacted here, as they are written, so the
/// shared secret and URL tokens never sit in the buffer at all.
/// </summary>
public sealed class RingBufferLoggerProvider(LogLineBuffer buffer, IReadOnlyList<string?> knownSecrets) : ILoggerProvider
{
    private readonly LogLineBuffer _buffer = buffer;
    private readonly IReadOnlyList<string?> _knownSecrets = knownSecrets;

    public ILogger CreateLogger(string categoryName)
    {
        return new RingBufferLogger(categoryName, _buffer, _knownSecrets);
    }

    public void Dispose()
    {
    }

    private sealed class RingBufferLogger(string categoryName, LogLineBuffer buffer, IReadOnlyList<string?> knownSecrets) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Information && logLevel != LogLevel.None;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            try
            {
                string message = LogRedactor.Redact(formatter(state, exception), knownSecrets);
                string? exceptionText = exception == null ? null : LogRedactor.Redact(exception.ToString(), knownSecrets);

                buffer.Add(DateTime.UtcNow, logLevel.ToString(), categoryName, message, exceptionText);
            }
            catch
            {
                // A logger must never throw into the code that is logging.
            }
        }
    }
}

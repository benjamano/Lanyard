using Lanyard.Shared.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Lanyard.Client.Diagnostics;

// Runs before the OTLP exporter so what leaves the PC is redacted the same way the diagnostics tail
// already is (Security Issue 9.10): tokens and credentials in a message or a logged value are
// replaced before the record is exported. Registered ahead of AddOtlpExporter, since log
// processors run in the order they're added.
public sealed class RedactingLogProcessor(IReadOnlyList<string?> knownSecrets) : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord record)
    {
        if (record.FormattedMessage is not null)
        {
            record.FormattedMessage = LogRedactor.Redact(record.FormattedMessage, knownSecrets);
        }

        if (record.Body is not null)
        {
            record.Body = LogRedactor.Redact(record.Body, knownSecrets);
        }

        if (record.Attributes is { Count: > 0 } attributes)
        {
            record.Attributes = attributes
                .Select(attribute => attribute.Value is string text
                    ? new KeyValuePair<string, object?>(attribute.Key, LogRedactor.Redact(text, knownSecrets))
                    : attribute)
                .ToList();
        }
    }
}

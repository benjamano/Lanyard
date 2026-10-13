using System.Text.Json;
using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Dmx.Ofl;

public interface IOflFixtureParser
{
    // An uploaded fixture file. Fails (never throws) on oversized, malformed or unusable JSON.
    Task<Result<OflParsedFixture>> ParseAsync(Stream json, string? oflKey, string? manufacturerName, CancellationToken cancellationToken = default);

    Result<OflParsedFixture> Parse(JsonElement fixture, string? oflKey, string? manufacturerName);
}

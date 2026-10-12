using System.Text.Json;
using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Dmx.Ofl;

public interface IOflFixtureParser
{
    // An uploaded fixture file. Fails (never throws) on oversized, malformed or unusable JSON.
    Result<OflParsedFixture> Parse(Stream json, string? oflKey, string? manufacturerName);

    Result<OflParsedFixture> Parse(JsonElement fixture, string? oflKey, string? manufacturerName);
}

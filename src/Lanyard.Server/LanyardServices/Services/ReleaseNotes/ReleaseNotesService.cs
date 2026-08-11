using System.Reflection;
using System.Text.Json;
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.DTO;

namespace Lanyard.Application.Services;

public class ReleaseNotesService : IReleaseNotesService
{
    private const string ResourceName = "Lanyard.Services.Services.ReleaseNotes.release-notes.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private List<ReleaseNote>? _cachedReleaseNotes;

    public Task<Result<IEnumerable<ReleaseNote>>> GetReleaseNotesAsync()
    {
        try
        {
            if (_cachedReleaseNotes is null)
            {
                Assembly assembly = Assembly.GetExecutingAssembly();

                using Stream? stream = assembly.GetManifestResourceStream(ResourceName);

                if (stream is null)
                {
                    return Task.FromResult(Result<IEnumerable<ReleaseNote>>.Fail($"Embedded resource '{ResourceName}' was not found."));
                }

                List<ReleaseNote>? releaseNotes = JsonSerializer.Deserialize<List<ReleaseNote>>(stream, SerializerOptions);

                _cachedReleaseNotes = (releaseNotes ?? [])
                    .OrderByDescending(x => x.ReleaseDate)
                    .ToList();
            }

            return Task.FromResult(Result<IEnumerable<ReleaseNote>>.Ok(_cachedReleaseNotes));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<IEnumerable<ReleaseNote>>.Fail($"An error occurred while retrieving release notes: {ex.Message}"));
        }
    }
}

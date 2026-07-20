using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services;

/// <summary>
/// Analyzes a song's audio for BPM and beat phase, persisting the outcome on the
/// Song row. Idempotent: songs already in a terminal analysis state are skipped.
/// </summary>
public interface ISongAnalysisService
{
    Task<Result<bool>> AnalyzeSongAsync(Guid songId, CancellationToken cancellationToken);
}

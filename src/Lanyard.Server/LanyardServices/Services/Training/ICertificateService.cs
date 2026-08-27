using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Training;

public interface ICertificateService
{
    /// <summary>
    /// Renders the completion certificate for an assignment as a PDF.
    /// </summary>
    /// <remarks>
    /// Authorised on ownership, not <c>LocationScope</c>: a certificate is a personal
    /// document, and a learner's own scope is simply their location - so a scope check
    /// would let every colleague at that location pull anyone else's certificate. This
    /// mirrors <see cref="ICourseAssignmentService.GetAssignmentAsync"/>'s rule instead.
    /// </remarks>
    Task<Result<byte[]>> GenerateCertificatePdfAsync(Guid assignmentId, string requestingUserId, CancellationToken cancellationToken = default);
}

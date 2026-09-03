namespace Lanyard.Shared;

/// <summary>
/// The contract between Lanyard.Reach.Web and the Lanyard server's ordering API.
///
/// These live in Lanyard.Shared, the only project both sides reference, because they are one
/// value with two ends rather than two independent settings. They were previously written out
/// by hand in each project and drifted: Reach read <c>Lanyard:ReachSecret</c> while the server
/// read <c>Reach:SharedSecret</c>, so setting the documented key configured exactly one side.
/// Reach started perfectly, was rejected on every ordering call, and customers were shown
/// "we couldn't find this table" - a missing environment variable reported as a bad QR code.
/// Referencing a single constant from both ends makes that particular mistake unavailable.
/// </summary>
public static class ReachApiConstants
{
    /// <summary>
    /// Configuration key for the shared secret, supplied in production as the environment
    /// variable <c>Reach__SharedSecret</c>. Must be identical on the Lanyard server and on Reach.
    /// </summary>
    public const string SharedSecretConfigurationKey = "Reach:SharedSecret";

    /// <summary>
    /// Header the secret travels in. Header only, never the query string, so it stays out of
    /// access logs.
    /// </summary>
    public const string SecretHeaderName = "X-Lanyard-Reach-Secret";
}

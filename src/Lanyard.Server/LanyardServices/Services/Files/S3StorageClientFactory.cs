using Amazon.S3;

namespace Lanyard.Application.Services;

/// <summary>
/// Builds the one <see cref="IAmazonS3"/> the app shares. <see cref="FileService"/> is scoped, so
/// creating the client in its constructor (as it used to) meant a new SDK client and HTTP pipeline
/// for every request, circuit and background scope that touched files.
/// </summary>
public static class S3StorageClientFactory
{
    public static IAmazonS3 CreateFromEnvironment()
    {
        string? endpointUrl = Environment.GetEnvironmentVariable("RAILWAY_BUCKET_ENDPOINT_URL");
        string? accessKey = Environment.GetEnvironmentVariable("RAILWAY_BUCKET_ACCESS_KEY_ID");
        string? secretKey = Environment.GetEnvironmentVariable("RAILWAY_BUCKET_SECRET_ACCESS_KEY");
        string? region = Environment.GetEnvironmentVariable("RAILWAY_BUCKET_REGION");

        if (string.IsNullOrWhiteSpace(endpointUrl))
            throw new InvalidOperationException("RAILWAY_BUCKET_ENDPOINT_URL is required in production.");

        if (string.IsNullOrWhiteSpace(accessKey))
            throw new InvalidOperationException("RAILWAY_BUCKET_ACCESS_KEY_ID is required in production.");

        if (string.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException("RAILWAY_BUCKET_SECRET_ACCESS_KEY is required in production.");

        AmazonS3Config config = new()
        {
            ServiceURL = endpointUrl,
            ForcePathStyle = true,
            AuthenticationRegion = region ?? "auto",
            UseHttp = false
        };

        return new AmazonS3Client(accessKey, secretKey, config);
    }
}

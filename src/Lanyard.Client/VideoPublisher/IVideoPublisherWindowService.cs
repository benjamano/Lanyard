namespace Lanyard.Client.VideoPublisher;

public interface IVideoPublisherWindowService
{
    void EnsureRunning(string publisherToken);
    void Stop();
}

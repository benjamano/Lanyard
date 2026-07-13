namespace Lanyard.Client.VideoPublisher;

public interface IVideoPublisherWindowService
{
    void EnsureRunning();
    void Stop();
}

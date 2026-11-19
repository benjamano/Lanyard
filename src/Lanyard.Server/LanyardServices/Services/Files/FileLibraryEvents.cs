namespace Lanyard.Application.Services;

/// <summary>
/// "Something in this company's Files changed": an upload, a rename, a move, a delete, a share.
/// Open Files pages listen and quietly refresh, so two people working at once (or one person in two
/// tabs) see each other's changes. In-process only, like the Clients page's push bus: every circuit
/// lives in this one server.
/// </summary>
public interface IFileLibraryEvents
{
    event Action<int>? Changed;

    void Publish(int companyId);
}

public sealed class FileLibraryEvents : IFileLibraryEvents
{
    public event Action<int>? Changed;

    public void Publish(int companyId)
    {
        Action<int>? handlers = Changed;

        if (handlers is null)
            return;

        // One slow or broken listener mustn't stop the rest hearing about it.
        foreach (Action<int> handler in handlers.GetInvocationList().Cast<Action<int>>())
        {
            try
            {
                handler(companyId);
            }
            catch (Exception)
            {
            }
        }
    }
}

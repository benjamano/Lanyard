using System.IO;

public static class ClientIdentity
{
    private const string ClientIdEnvironmentVariable = "LANYARD_CLIENT_ID";
    private const string ClientIdFileName = "client-id.txt";

    // Outside Velopack's install folder, so the kiosk keeps its id (and its credential, which
    // lives alongside) through updates.
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "lanyardClient");

    public static string ClientIdPath => Path.Combine(DataDirectory, ClientIdFileName);

    public static Guid LoadOrCreateClientId()
    {
        Directory.CreateDirectory(DataDirectory);

        string path = ClientIdPath;

        Guid savedClientId;
        if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path), out savedClientId))
        {
            return savedClientId;
        }

        Guid newClientId = Guid.NewGuid();
        File.WriteAllText(path, newClientId.ToString());

        return newClientId;
    }

    public static void ApplyToEnvironment(Guid clientId)
    {
        Environment.SetEnvironmentVariable(ClientIdEnvironmentVariable, clientId.ToString());
    }
}
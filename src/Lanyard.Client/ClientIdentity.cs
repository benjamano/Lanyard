public static class ClientIdentity
{
    private const string ClientIdEnvironmentVariable = "LANYARD_CLIENT_ID";
    private const string ClientIdFileName = "client-id.txt";

    public static Guid LoadOrCreateClientId()
    {
        string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "lanyardClient");
        Directory.CreateDirectory(baseDir);

        string path = Path.Combine(baseDir, ClientIdFileName);

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
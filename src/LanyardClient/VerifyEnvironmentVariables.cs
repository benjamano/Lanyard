using System.Text.Json;

public class VerifyEnvironmentVariables
{
    public static void Check()
    {
        List<(string, string)> environmentVariables = new List<(string, string)>
        {
            ("LANYARD_SERVER_URL", "https://localhost:7175")
        };

        string configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LanyardClient",
            "config.json"
        );

        Console.WriteLine("Reading config from " + configPath);

        Dictionary<string, string> config = new Dictionary<string, string>();

        if (File.Exists(configPath))
        {
            string json = File.ReadAllText(configPath);
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    Dictionary<string, string>? loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (loaded != null)
                    {
                        config = loaded;
                    }
                }
                catch (JsonException)
                {
                    Console.WriteLine("Warning: config.json is not valid JSON. It will be overwritten.");
                }
            }
        }

        foreach ((string envVar, string defaultValue) in environmentVariables)
        {
            config.TryGetValue(envVar, out string? variable);

            if (string.IsNullOrWhiteSpace(variable))
            {
                variable = Environment.GetEnvironmentVariable(envVar);
            }

            while (string.IsNullOrWhiteSpace(variable))
            {
                Console.WriteLine($"Please set the {envVar} (Press enter for the default: {defaultValue}): ");

                variable = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(variable))
                {
                    Console.WriteLine($"Using default value for {envVar}: {defaultValue}");

                    variable = defaultValue;
                }
            }

            config[envVar] = variable!;

            Environment.SetEnvironmentVariable(envVar, variable);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, 
            new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void ResetClientId()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LanyardClient",
            "client_id.txt"
        );

        Guid newClientId = Guid.NewGuid();

        File.WriteAllText(path, newClientId.ToString());
        Environment.SetEnvironmentVariable("LANYARD_CLIENT_ID", newClientId.ToString());

        Console.WriteLine($"Client ID reset to {newClientId}. Please restart the application for changes to take effect.");

        throw new Exception("Client ID reset. Please restart the application.");
    }

    public static void ResetConfig()
    {
        string configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LanyardClient",
            "config.json"
        );

        if (File.Exists(configPath))
        {
            File.Delete(configPath);
            Console.WriteLine("Config reset. All saved environment variables have been cleared. Please restart the application and set the required environment variables.");

            throw new Exception("Config reset. Please restart the application.");
        }
        else
        {
            Console.WriteLine("No config file found to reset.");
        }
    }
}
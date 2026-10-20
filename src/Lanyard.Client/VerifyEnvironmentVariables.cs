using System.IO;
using System.Text.Json;

public class VerifyEnvironmentVariables
{
    public static void Check()
    {
        List<(string, string)> environmentVariables = new List<(string, string)>
        {
            ("LANYARD_SERVER_URL", "https://localhost:7175"),
            ("LANYARD_CLIENT_ALLOW_INSECURE_SSL", "false"),
            ("LANYARD_CLIENT_SKIP_ADDING_WATCHDOG_STARTUP_TASK", "false"),
            // Only for a kiosk that hasn't got its own credential yet: it swaps this for one once,
            // then it's removed (RemoveLegacySecret). New kiosks pair with a code instead.
            ("LANYARD_CLIENT_SHARED_SECRET", ""),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", ""),
            ("OTEL_EXPORTER_OTLP_PROTOCOL", "")
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

            if (string.IsNullOrWhiteSpace(variable))
            {
                if (!string.IsNullOrWhiteSpace(defaultValue))
                {
                    Console.WriteLine($"Using default value for {envVar}: {defaultValue}");
                }

                variable = defaultValue;
            }

            config[envVar] = variable;

            // Some vars (e.g. the OTLP ones) are genuinely optional — leave the process
            // environment untouched rather than setting it to an empty string, which would
            // override a real value already set at the OS/session level.
            if (!string.IsNullOrWhiteSpace(variable))
            {
                Environment.SetEnvironmentVariable(envVar, variable);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, 
            new JsonSerializerOptions { WriteIndented = true }));
    }

    // The shared secret has done its job once the kiosk has its own credential: keep it out of
    // config.json (and this process) from then on.
    public static void RemoveLegacySecret()
    {
        const string variable = "LANYARD_CLIENT_SHARED_SECRET";

        Environment.SetEnvironmentVariable(variable, null);

        string configPath = ConfigPath;

        if (!File.Exists(configPath))
        {
            return;
        }

        try
        {
            Dictionary<string, string>? config = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(configPath));

            if (config is not null && config.Remove(variable))
            {
                File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("Removed the old shared secret from config.json: this kiosk has its own credential now.");
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.WriteLine($"Couldn't remove the old shared secret from config.json: {ex.Message}");
        }
    }

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LanyardClient",
        "config.json");

    public static void ResetClientId()
    {
        // The file ClientIdentity actually reads. A new id is a new kiosk to the server, so the
        // old credential goes too and the kiosk pairs again.
        string path = ClientIdentity.ClientIdPath;

        Guid newClientId = Guid.NewGuid();

        Directory.CreateDirectory(ClientIdentity.DataDirectory);
        File.WriteAllText(path, newClientId.ToString());
        Lanyard.Client.Security.KioskCredentials.Forget();
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
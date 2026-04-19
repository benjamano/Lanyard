using System.Text.Json;

public class VerifyEnvironmentVariables
{
    public static void Check()
    {
        string[] environmentVariables = new[]
        {
            "SIGNALR_SERVER_URL",
            "KIOSK_SERVER_URL",
            "API_SERVER_URL",
        };

        string configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LanyardClient",
            "config.json"
        );

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

        foreach (string envVar in environmentVariables)
        {
            string? variable = null;

            config.TryGetValue(envVar, out variable);

            if (string.IsNullOrWhiteSpace(variable))
            {
                variable = Environment.GetEnvironmentVariable(envVar);
            }

            while (string.IsNullOrWhiteSpace(variable))
            {
                Console.WriteLine($"Please set the {envVar}: ");
                variable = Console.ReadLine();
            }

            config[envVar] = variable!;

            Environment.SetEnvironmentVariable(envVar, variable);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, 
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
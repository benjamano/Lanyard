public static class StartupControls
{
    public static async Task ShowIfInterruptedAsync()
    {
        if (await CountdownWithInterruptAsync())
        {
            ShowControls();
        }
    }

    private static void ShowControls()
    {
        int? option = null;

        while (option == null)
        {
            Console.WriteLine("Controls:");
            Console.WriteLine("1. Reset client ID (This will create a new client ID and disconnect from the server if already connected)");
            Console.WriteLine("2. Reset Config (This will clear all saved environment variables and ask for them again on next startup)");
            Console.WriteLine("3. Continue with normal startup");
            Console.WriteLine("Enter an option: ");

            string? input = Console.ReadLine();

            int selected;
            if (int.TryParse(input, out selected))
            {
                option = selected;
                HandleControlOption(selected);
            }
            else
            {
                Console.WriteLine("Invalid input. Please enter a number corresponding to an option.");
            }
        }
    }

    private static void HandleControlOption(int option)
    {
        switch (option)
        {
            case 1:
                VerifyEnvironmentVariables.ResetClientId();
                throw new InvalidOperationException("Client ID reset. Please restart the application.");
            case 2:
                VerifyEnvironmentVariables.ResetConfig();
                throw new InvalidOperationException("Config reset. Please restart the application.");
            case 3:
                Console.WriteLine("Continuing with normal startup...");
                break;
            default:
                Console.WriteLine("Unknown option selected.");
                break;
        }
    }

    private static async Task<bool> CountdownWithInterruptAsync()
    {
        int countdown = 5;

        Console.WriteLine("Press any key to interrupt startup and access controls.");

        while (countdown > 0)
        {
            Console.WriteLine($"Starting in {countdown} seconds...");
            await Task.Delay(1000);
            countdown--;

            if (!Console.IsInputRedirected)
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline)
                {
                    if (Console.KeyAvailable)
                    {
                        Console.ReadKey(intercept: true);
                        Console.WriteLine("Startup interrupted. Accessing controls...");
                        return true;
                    }

                    await Task.Delay(50);
                }
            }
        }

        return false;
    }
}
namespace Lanyard.Application.Services.Email;

public class EmailOptions
{
    public string ResendApiKey { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "Lanyard";

    // Used only by background/hosted-service-triggered emails that have no live
    // circuit (and therefore no NavigationManager) to build a link from.
    public string PublicBaseUrl { get; set; } = string.Empty;
}

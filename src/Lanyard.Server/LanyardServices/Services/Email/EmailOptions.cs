namespace Lanyard.Application.Services.Email;

public class EmailOptions
{
    public string ResendApiKey { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "Lanyard";
}

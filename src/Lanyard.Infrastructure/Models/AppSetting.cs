#nullable enable

namespace Lanyard.Infrastructure.Models;

public class AppSetting
{
    public Guid Id { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTime CreateDate { get; set; }
}

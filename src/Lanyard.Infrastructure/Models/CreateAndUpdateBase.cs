using System.Runtime.Intrinsics.X86;
using Lanyard.Infrastructure.Models;

public class CreateAndUpdateBase
{
    public DateTime CreateDate { get; set; }
    public required string CreateByUserId { get; set; }
    public UserProfile? CreateByUser { get; set; }

    public DateTime? UpdateDate { get; set; }
    public string? UpdateByUserId { get; set; }
    public UserProfile? UpdateByUser { get; set; }
}
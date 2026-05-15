using System.Runtime.Intrinsics.X86;
using Lanyard.Infrastructure.Models;

public class CreateAndUpdateBase
{
    public DateTime CreateDate { get; set; }
    public required string CreatedByUserId { get; set; }
    public UserProfile? CreateByUser { get; set; }

    public DateTime? UpdateDate { get; set; }
    public required string UpdatedByUserId { get; set; }
    public UserProfile? UpdatedByUser { get; set; }
}
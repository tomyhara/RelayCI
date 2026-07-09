namespace CiRunner.Core.Models;

public sealed class ResourceDefRecord
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

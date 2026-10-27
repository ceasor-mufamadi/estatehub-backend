namespace EstateHub.Domain.Entities;

/// <summary>A managing agent (or a self-managed scheme) that can own several estates.</summary>
public class Organisation : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

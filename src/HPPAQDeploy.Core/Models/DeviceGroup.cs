namespace HPPAQDeploy.Core.Models;

public class DeviceGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;

    public override string ToString() => Name;

    public override bool Equals(object? obj)
    {
        if (obj is not DeviceGroup other) return false;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id.GetHashCode();
}

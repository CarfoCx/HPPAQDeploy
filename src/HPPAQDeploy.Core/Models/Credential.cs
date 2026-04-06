namespace HPPAQDeploy.Core.Models;

public class Credential
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public byte[] EncryptedPassword { get; set; } = Array.Empty<byte>();
    public byte[] IV { get; set; } = Array.Empty<byte>();
    public DateTime Created { get; set; }
    public bool IsDefault { get; set; }

    public override string ToString() => Label;

    public override bool Equals(object? obj)
    {
        if (obj is not Credential other) return false;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id.GetHashCode();
}

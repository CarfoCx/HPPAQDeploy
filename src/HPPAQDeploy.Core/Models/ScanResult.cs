namespace HPPAQDeploy.Core.Models;

public class ScanResult
{
    public string IpAddress { get; set; } = string.Empty;
    public bool IsAlive { get; set; }
    public int ResponseTimeMs { get; set; }
    public string? Hostname { get; set; }
}

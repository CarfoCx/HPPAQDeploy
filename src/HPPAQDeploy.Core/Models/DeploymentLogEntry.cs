namespace HPPAQDeploy.Core.Models;

public class DeploymentLogEntry
{
    public DateTime Timestamp { get; set; }
    public string DeviceName { get; set; } = "";
    public string Message { get; set; } = "";
    public string Level { get; set; } = "Info";
}

namespace HPPAQDeploy.Core.Models;

public class DeploymentJob
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int TotalUpdates { get; set; }
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public string Status { get; set; } = string.Empty;
    public string LogOutput { get; set; } = string.Empty;
}

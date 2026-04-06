namespace HPPAQDeploy.Core.Models;

public class DeploymentHistory
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public string DeviceHostname { get; set; } = "";
    public string DeviceIpAddress { get; set; } = "";
    public string UpdateName { get; set; } = "";
    public string SoftPaqId { get; set; } = "";
    public string Category { get; set; } = "";
    public string Action { get; set; } = ""; // "Deployed", "Failed", "Cancelled"
    public string? ErrorMessage { get; set; }
    public DateTime Timestamp { get; set; }
    public bool RebootRequired { get; set; }
}

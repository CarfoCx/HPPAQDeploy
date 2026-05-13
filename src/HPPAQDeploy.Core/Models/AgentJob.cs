namespace HPPAQDeploy.Core.Models;

public enum AgentJobType
{
    Scan,
    Install,
    Status
}

public enum AgentJobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public sealed class AgentJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AgentJobType Type { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<string> SoftPaqIds { get; set; } = [];
}

public sealed class AgentJobResult
{
    public string JobId { get; set; } = string.Empty;
    public AgentJobType Type { get; set; }
    public AgentJobStatus Status { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime CompletedUtc { get; set; }
    public int ExitCode { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<HpiaRecommendation> Recommendations { get; set; } = [];
}

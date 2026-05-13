using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.Core.Interfaces;

public interface IAgentClient
{
    Task<string> SubmitScanAsync(string hostname, AgentJob job, CancellationToken ct);

    Task<string> SubmitInstallAsync(string hostname, AgentJob job, CancellationToken ct);

    Task<AgentJobResult?> TryGetResultAsync(string hostname, string jobId, CancellationToken ct);

    Task<string> GetJobStateAsync(string hostname, string jobId, CancellationToken ct);
}

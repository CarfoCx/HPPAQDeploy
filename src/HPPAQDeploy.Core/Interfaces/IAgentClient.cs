using HPPAQDeploy.Core.Models;
using System.Net;

namespace HPPAQDeploy.Core.Interfaces;

public interface IAgentClient
{
    Task<string> SubmitScanAsync(string hostname, NetworkCredential credential, AgentJob job, CancellationToken ct);

    Task<string> SubmitInstallAsync(string hostname, NetworkCredential credential, AgentJob job, CancellationToken ct);

    Task<AgentJobResult?> TryGetResultAsync(string hostname, NetworkCredential credential, string jobId, CancellationToken ct);

    Task<string> GetJobStateAsync(string hostname, NetworkCredential credential, string jobId, CancellationToken ct);
}

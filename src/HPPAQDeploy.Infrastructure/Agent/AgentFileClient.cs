using System.Text.Json;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using Serilog;

namespace HPPAQDeploy.Infrastructure.Agent;

public sealed class AgentFileClient : IAgentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ILogger _logger = Log.ForContext<AgentFileClient>();

    public Task<string> SubmitScanAsync(string hostname, AgentJob job, CancellationToken ct)
    {
        job.Type = AgentJobType.Scan;
        return SubmitAsync(hostname, job, ct);
    }

    public Task<string> SubmitInstallAsync(string hostname, AgentJob job, CancellationToken ct)
    {
        job.Type = AgentJobType.Install;
        return SubmitAsync(hostname, job, ct);
    }

    public async Task<AgentJobResult?> TryGetResultAsync(string hostname, string jobId, CancellationToken ct)
    {
        var resultPath = Path.Combine(GetResultsUnc(hostname), $"{jobId}.json");
        if (!File.Exists(resultPath))
            return null;

        await using var stream = File.OpenRead(resultPath);
        return await JsonSerializer.DeserializeAsync<AgentJobResult>(stream, JsonOptions, ct);
    }

    private async Task<string> SubmitAsync(string hostname, AgentJob job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.Id))
            job.Id = Guid.NewGuid().ToString("N");

        var jobsPath = GetJobsUnc(hostname);
        Directory.CreateDirectory(jobsPath);
        Directory.CreateDirectory(GetResultsUnc(hostname));

        var finalPath = Path.Combine(jobsPath, $"{job.Id}.json");
        var tempPath = finalPath + ".tmp";

        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(job, JsonOptions), ct);
        File.Move(tempPath, finalPath, overwrite: true);

        _logger.Information("Submitted agent job {JobId} ({Type}) to {Hostname}", job.Id, job.Type, hostname);
        return job.Id;
    }

    private static string GetJobsUnc(string hostname)
        => $@"\\{hostname}\C$\ProgramData\HPPAQDeploy\jobs";

    private static string GetResultsUnc(string hostname)
        => $@"\\{hostname}\C$\ProgramData\HPPAQDeploy\results";
}

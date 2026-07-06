using System.Text.Json;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Infrastructure.Remote;
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

        try
        {
            await using var stream = File.OpenRead(resultPath);
            return await JsonSerializer.DeserializeAsync<AgentJobResult>(stream, JsonOptions, ct);
        }
        catch (IOException ex) when (ex is not FileNotFoundException)
        {
            // File may still be written by agent — retry on next poll
            _logger.Debug(ex, "Result file for {JobId} on {Hostname} not ready yet", jobId, hostname);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.Warning(ex, "Corrupt result file for {JobId} on {Hostname}", jobId, hostname);
            // Delete the corrupt file so it doesn't block future checks
            try { File.Delete(resultPath); } catch { }
            return null;
        }
    }

    public Task<string> GetJobStateAsync(string hostname, string jobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            if (File.Exists(Path.Combine(GetResultsUnc(hostname), $"{jobId}.json")))
                return Task.FromResult("result ready");

            if (File.Exists(Path.Combine(GetJobsUnc(hostname), $"{jobId}.running")))
                return Task.FromResult("running");

            if (File.Exists(Path.Combine(GetJobsUnc(hostname), $"{jobId}.json")))
                return Task.FromResult("queued");
        }
        catch (IOException ex)
        {
            _logger.Debug(ex, "IO error checking job state for {JobId} on {Hostname}", jobId, hostname);
            return Task.FromResult("checking");
        }

        return Task.FromResult("job file not found");
    }

    private async Task<string> SubmitAsync(string hostname, AgentJob job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.Id))
            job.Id = Guid.NewGuid().ToString("N");

        var jobsPath = GetJobsUnc(hostname);
        Directory.CreateDirectory(jobsPath);
        Directory.CreateDirectory(GetResultsUnc(hostname));

        // Clean up stale job files for THIS job type before submitting
        // This prevents the agent from processing old leftover jobs
        CleanStaleJobFiles(jobsPath, job.Type);

        var finalPath = Path.Combine(jobsPath, $"{job.Id}.json");
        var tempPath = finalPath + ".tmp";

        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(job, JsonOptions), ct);
        File.Move(tempPath, finalPath, overwrite: true);

        _logger.Information("Submitted agent job {JobId} ({Type}) to {Hostname}", job.Id, job.Type, hostname);
        return job.Id;
    }

    /// <summary>
    /// Removes old .running files and stale job files of the same type
    /// that might interfere with new job processing.
    /// </summary>
    private void CleanStaleJobFiles(string jobsPath, AgentJobType type)
    {
        try
        {
            // Clean up any stale .running files (agent crashed/timed out on previous run)
            foreach (var runningFile in Directory.GetFiles(jobsPath, "*.running"))
            {
                try
                {
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(runningFile);
                    if (age.TotalMinutes > 10)
                    {
                        File.Delete(runningFile);
                        _logger.Debug("Deleted stale running file: {File}", Path.GetFileName(runningFile));
                    }
                }
                catch { }
            }

            // Clean up old job files of the same type that are more than 5 minutes old
            foreach (var jobFile in Directory.GetFiles(jobsPath, "*.json"))
            {
                try
                {
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(jobFile);
                    if (age.TotalMinutes > 5)
                    {
                        var content = File.ReadAllText(jobFile);
                        if (content.Contains($"\"{type}\"", StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(jobFile);
                            _logger.Debug("Deleted stale job file: {File}", Path.GetFileName(jobFile));
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Error cleaning stale job files");
        }
    }

    private static string GetJobsUnc(string hostname)
        => $@"\\{hostname}\C$\ProgramData\HPPAQDeploy\jobs";

    private static string GetResultsUnc(string hostname)
        => $@"\\{hostname}\C$\ProgramData\HPPAQDeploy\results";
}

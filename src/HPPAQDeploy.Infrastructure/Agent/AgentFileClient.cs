using System.Net;
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
    private readonly IFileTransfer? _fileTransfer;
    private readonly Func<string, string> _getJobsPath;
    private readonly Func<string, string> _getResultsPath;

    public AgentFileClient(IFileTransfer fileTransfer)
        : this(fileTransfer, GetJobsUnc, GetResultsUnc)
    {
    }

    internal AgentFileClient(Func<string, string> getJobsPath, Func<string, string> getResultsPath)
        : this(null, getJobsPath, getResultsPath)
    {
    }

    internal AgentFileClient(
        IFileTransfer? fileTransfer,
        Func<string, string> getJobsPath,
        Func<string, string> getResultsPath)
    {
        _fileTransfer = fileTransfer;
        _getJobsPath = getJobsPath ?? throw new ArgumentNullException(nameof(getJobsPath));
        _getResultsPath = getResultsPath ?? throw new ArgumentNullException(nameof(getResultsPath));
    }

    public Task<string> SubmitScanAsync(
        string hostname,
        NetworkCredential credential,
        AgentJob job,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        job.Type = AgentJobType.Scan;
        return SubmitAsync(hostname, credential, job, ct);
    }

    public Task<string> SubmitInstallAsync(
        string hostname,
        NetworkCredential credential,
        AgentJob job,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        job.Type = AgentJobType.Install;
        return SubmitAsync(hostname, credential, job, ct);
    }

    public async Task<AgentJobResult?> TryGetResultAsync(
        string hostname,
        NetworkCredential credential,
        string jobId,
        CancellationToken ct)
    {
        ValidateHostname(hostname);
        ArgumentNullException.ThrowIfNull(credential);
        ValidateJobId(jobId);
        await using var remoteSession = await OpenRemoteSessionAsync(hostname, credential, ct)
            .ConfigureAwait(false);
        var resultPath = Path.Combine(_getResultsPath(hostname), $"{jobId}.json");
        if (!File.Exists(resultPath))
            return null;

        try
        {
            await using var stream = new FileStream(
                resultPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);
            var result = await JsonSerializer.DeserializeAsync<AgentJobResult>(stream, JsonOptions, ct)
                .ConfigureAwait(false);

            if (result is not null &&
                !string.IsNullOrWhiteSpace(result.JobId) &&
                !string.Equals(result.JobId, jobId, StringComparison.Ordinal))
            {
                _logger.Warning(
                    "Ignoring result file for {JobId} on {Hostname} because it contains JobId {ActualJobId}",
                    jobId,
                    hostname,
                    result.JobId);
                return null;
            }

            return result;
        }
        catch (IOException ex)
        {
            // File may still be written by agent — retry on next poll
            _logger.Debug(ex, "Result file for {JobId} on {Hostname} not ready yet", jobId, hostname);
            return null;
        }
        catch (JsonException ex)
        {
            // Older agents wrote directly to the final path, so malformed JSON can
            // simply mean the writer has not finished. Leave it for the next poll.
            _logger.Debug(ex, "Result file for {JobId} on {Hostname} is not complete yet", jobId, hostname);
            return null;
        }
    }

    public async Task<string> GetJobStateAsync(
        string hostname,
        NetworkCredential credential,
        string jobId,
        CancellationToken ct)
    {
        ValidateHostname(hostname);
        ArgumentNullException.ThrowIfNull(credential);
        ValidateJobId(jobId);
        ct.ThrowIfCancellationRequested();
        await using var remoteSession = await OpenRemoteSessionAsync(hostname, credential, ct)
            .ConfigureAwait(false);

        try
        {
            if (File.Exists(Path.Combine(_getResultsPath(hostname), $"{jobId}.json")))
                return "result ready";

            if (File.Exists(Path.Combine(_getJobsPath(hostname), $"{jobId}.running")))
                return "running";

            if (File.Exists(Path.Combine(_getJobsPath(hostname), $"{jobId}.json")))
                return "queued";
        }
        catch (IOException ex)
        {
            _logger.Debug(ex, "IO error checking job state for {JobId} on {Hostname}", jobId, hostname);
            return "checking";
        }

        return "job file not found";
    }

    private async Task<string> SubmitAsync(
        string hostname,
        NetworkCredential credential,
        AgentJob job,
        CancellationToken ct)
    {
        ValidateHostname(hostname);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrWhiteSpace(job.Id))
            job.Id = Guid.NewGuid().ToString("N");
        ValidateJobId(job.Id);
        await using var remoteSession = await OpenRemoteSessionAsync(hostname, credential, ct)
            .ConfigureAwait(false);

        var jobsPath = _getJobsPath(hostname);
        Directory.CreateDirectory(jobsPath);
        Directory.CreateDirectory(_getResultsPath(hostname));

        var finalPath = Path.Combine(jobsPath, $"{job.Id}.json");
        var tempPath = finalPath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(job, JsonOptions), ct)
                .ConfigureAwait(false);
            File.Move(tempPath, finalPath, overwrite: false);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }

        _logger.Information("Submitted agent job {JobId} ({Type}) to {Hostname}", job.Id, job.Type, hostname);
        return job.Id;
    }

    private Task<IRemoteFileSession?> OpenRemoteSessionAsync(
        string hostname,
        NetworkCredential credential,
        CancellationToken ct)
    {
        if (_fileTransfer is null)
            return Task.FromResult<IRemoteFileSession?>(null);

        return OpenRemoteSessionCoreAsync(hostname, credential, ct);
    }

    private async Task<IRemoteFileSession?> OpenRemoteSessionCoreAsync(
        string hostname,
        NetworkCredential credential,
        CancellationToken ct) =>
        await _fileTransfer!.OpenAuthenticatedSessionAsync(hostname, credential, ct)
            .ConfigureAwait(false);

    private static void ValidateHostname(string hostname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        if (hostname.Length > 255 ||
            hostname is "." or ".." ||
            hostname.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("Hostname contains invalid path characters.", nameof(hostname));
    }

    private static void ValidateJobId(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        if (jobId.Length > 128 || jobId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Job ID contains invalid path characters.", nameof(jobId));
    }

    private static string GetJobsUnc(string hostname)
        => $@"\\{hostname}\C$\ProgramData\HPPAQDeploy\jobs";

    private static string GetResultsUnc(string hostname)
        => $@"\\{hostname}\C$\ProgramData\HPPAQDeploy\results";
}

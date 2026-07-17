using System.Diagnostics;
using System.Text.Json;
using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Infrastructure.Hpia;
using HPPAQDeploy.Shared.Helpers;
using Serilog;

namespace HPPAQDeploy.Agent;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string AgentRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "HPPAQDeploy");
    private static readonly string JobsPath = Path.Combine(AgentRoot, "jobs");
    private static readonly string ResultsPath = Path.Combine(AgentRoot, "results");
    private static readonly string ReportsPath = Path.Combine(AgentRoot, "reports");
    private static readonly string DownloadsPath = Path.Combine(AgentRoot, "downloads");
    private static readonly string LogsPath = Path.Combine(AgentRoot, "logs");
    private static readonly string HpiaPath = Path.Combine(AgentRoot, "HPIA");

    private static async Task<int> Main(string[] args)
    {
        Directory.CreateDirectory(AgentRoot);
        Directory.CreateDirectory(JobsPath);
        Directory.CreateDirectory(ResultsPath);
        Directory.CreateDirectory(ReportsPath);
        Directory.CreateDirectory(DownloadsPath);
        Directory.CreateDirectory(LogsPath);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(LogsPath, "agent-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "run-once";

            if (mode is "watch" or "run-once")
            {
                using var runnerLease = TryAcquireRunnerLease();
                if (runnerLease is null)
                {
                    Log.Information("Another agent process is already running; no work was started");
                    return 0;
                }

                RecoverInterruptedJobs();

                return mode == "watch"
                    ? await WatchAsync()
                    : await RunOnceAsync();
            }

            return mode switch
            {
                "status" => WriteStatus(),
                _ => Usage()
            };
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Agent failed");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: HPPAQDeploy.Agent.exe [run-once|watch|status]");
        return 2;
    }

    private static int WriteStatus()
    {
        var status = new
        {
            status = "ready",
            machine = Environment.MachineName,
            root = AgentRoot,
            hpia = TryResolveHpiaExe() ?? "not found"
        };
        Console.WriteLine(JsonSerializer.Serialize(status, JsonOptions));
        return 0;
    }

    private static FileStream? TryAcquireRunnerLease()
    {
        var leasePath = Path.Combine(AgentRoot, "agent.lock");
        try
        {
            return new FileStream(
                leasePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<int> WatchAsync()
    {
        Log.Information("HPPAQDeploy agent watch started at {Root}", AgentRoot);
        while (true)
        {
            await RunOnceAsync();
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<int> RunOnceAsync()
    {
        var jobFiles = Directory.GetFiles(JobsPath, "*.json")
            .OrderBy(File.GetCreationTimeUtc)
            .ToList();

        foreach (var jobFile in jobFiles)
        {
            await ProcessJobFileAsync(jobFile);
        }

        return 0;
    }

    private static void RecoverInterruptedJobs()
    {
        foreach (var runningPath in Directory.GetFiles(JobsPath, "*.running"))
        {
            var jobPath = Path.ChangeExtension(runningPath, ".json");
            if (File.Exists(jobPath))
            {
                Log.Warning(
                    "Cannot recover interrupted job {RunningPath} because {JobPath} already exists",
                    runningPath,
                    jobPath);
                continue;
            }

            File.Move(runningPath, jobPath);
            Log.Warning("Recovered interrupted agent job {JobPath}", jobPath);
        }
    }

    private static async Task ProcessJobFileAsync(string jobFile)
    {
        AgentJob? job = null;
        var runningPath = Path.ChangeExtension(jobFile, ".running");
        var claimed = false;
        var resultPublished = false;

        try
        {
            // Claim the job before reading it. Multiple scheduled-task invocations can
            // overlap, and only the process that wins this atomic move may execute it.
            File.Move(jobFile, runningPath);
            claimed = true;

            job = JsonSerializer.Deserialize<AgentJob>(await File.ReadAllTextAsync(runningPath), JsonOptions)
                ?? throw new InvalidOperationException("Job file is empty or invalid.");

            ValidateJobId(job.Id);
            if (!string.Equals(
                    Path.GetFileNameWithoutExtension(jobFile),
                    job.Id,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The job ID does not match its file name.");
            }

            Log.Information("Processing agent job {JobId} ({Type})", job.Id, job.Type);
            var result = job.Type switch
            {
                AgentJobType.Scan => await RunScanAsync(job),
                AgentJobType.Install => await RunInstallAsync(job),
                AgentJobType.Status => CreateStatusResult(job),
                _ => throw new InvalidOperationException($"Unsupported job type {job.Type}.")
            };

            await WriteResultAsync(result);
            resultPublished = true;
        }
        catch (IOException) when (!claimed && !File.Exists(jobFile))
        {
            // Another agent invocation claimed the job first.
            Log.Debug("Job file {JobFile} was claimed by another agent process", jobFile);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to process job file {JobFile}", jobFile);
            if (job is not null)
            {
                try
                {
                    await WriteResultAsync(new AgentJobResult
                    {
                        JobId = job.Id,
                        Type = job.Type,
                        Status = AgentJobStatus.Failed,
                        StartedUtc = DateTime.UtcNow,
                        CompletedUtc = DateTime.UtcNow,
                        ExitCode = -1,
                        Message = ex.Message
                    });
                    resultPublished = true;
                }
                catch (Exception resultEx)
                {
                    Log.Error(resultEx, "Failed to publish failure result for job {JobId}", job.Id);
                }
            }
        }
        finally
        {
            if (claimed && resultPublished)
            {
                try { File.Delete(runningPath); }
                catch (Exception ex) { Log.Warning(ex, "Failed to remove running marker {RunningPath}", runningPath); }
            }
        }
    }

    private static AgentJobResult CreateStatusResult(AgentJob job) => new()
    {
        JobId = job.Id,
        Type = job.Type,
        Status = AgentJobStatus.Succeeded,
        StartedUtc = DateTime.UtcNow,
        CompletedUtc = DateTime.UtcNow,
        Message = "Agent ready"
    };

    private static async Task<AgentJobResult> RunScanAsync(AgentJob job)
    {
        var started = DateTime.UtcNow;
        ClearDirectory(ReportsPath);
        ClearDirectory(DownloadsPath);

        var hpiaExe = ResolveHpiaExe();
        var offlineArg = GetOfflineModeArgument(job);
        var args =
            "/Operation:Analyze /Category:All /Selection:All /Action:List " +
            "/Silent /Noninteractive /ReportFormat:JSON " +
            $"/ReportFolder:\"{ReportsPath}\" /Debug /LogFolder:\"{LogsPath}\"" +
            offlineArg;

        var exitCode = await RunProcessAsync(hpiaExe, args, TimeSpan.FromMinutes(45));
        var recommendations = new HpiaReportParser().ParseReportDirectory(ReportsPath, 0);
        var success = HpiaExitCodes.IsSuccess(exitCode) || recommendations.Count > 0;

        return new AgentJobResult
        {
            JobId = job.Id,
            Type = job.Type,
            Status = success ? AgentJobStatus.Succeeded : AgentJobStatus.Failed,
            StartedUtc = started,
            CompletedUtc = DateTime.UtcNow,
            ExitCode = exitCode,
            Message = success
                ? $"Scan completed. {recommendations.Count} update(s) found."
                : $"HPIA scan failed: {HpiaExitCodes.GetMessage(exitCode)}",
            Recommendations = recommendations
        };
    }

    private static async Task<AgentJobResult> RunInstallAsync(AgentJob job)
    {
        var started = DateTime.UtcNow;
        var spListPath = Path.Combine(AgentRoot, "splist.txt");
        var numericIds = job.SoftPaqIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Select(id => id.StartsWith("sp", StringComparison.OrdinalIgnoreCase) ? id[2..] : id)
            .Where(id => id.Length > 0 && id.All(char.IsAsciiDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (numericIds.Count == 0)
            throw new InvalidOperationException("The install job does not contain any valid SoftPaq IDs.");

        ClearDirectory(ReportsPath);
        ClearDirectory(DownloadsPath);

        var hpiaExe = ResolveHpiaExe();
        await File.WriteAllLinesAsync(spListPath, numericIds);

        var offlineArg = GetOfflineModeArgument(job);
        var args =
            "/Operation:Analyze /Action:Install /Silent /Noninteractive " +
            $"/SoftpaqDownloadFolder:\"{DownloadsPath}\" /ReportFolder:\"{ReportsPath}\" " +
            $"/Debug /LogFolder:\"{LogsPath}\" /SPList:\"{spListPath}\"" +
            offlineArg;

        var exitCode = await RunProcessAsync(hpiaExe, args, TimeSpan.FromHours(2));
        var success = HpiaExitCodes.IsSuccess(exitCode);

        return new AgentJobResult
        {
            JobId = job.Id,
            Type = job.Type,
            Status = success ? AgentJobStatus.Succeeded : AgentJobStatus.Failed,
            StartedUtc = started,
            CompletedUtc = DateTime.UtcNow,
            ExitCode = exitCode,
            Message = success
                ? HpiaExitCodes.GetMessage(exitCode)
                : $"HPIA install failed: {HpiaExitCodes.GetMessage(exitCode)}"
        };
    }

    private static async Task<int> RunProcessAsync(string fileName, string arguments, TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(fileName) ?? AgentRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        Log.Information("Running HPIA: {FileName} {Arguments}", fileName, arguments);
        process.Start();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Timed-out HPIA process did not stop cleanly");
            }

            return -1;
        }
    }

    private static string ResolveHpiaExe()
        => TryResolveHpiaExe()
           ?? throw new FileNotFoundException(
               $"HPImageAssistant.exe was not found. Place HPIA under {HpiaPath} or beside the agent.");

    private static string? TryResolveHpiaExe()
    {
        var candidates = new[]
        {
            Path.Combine(HpiaPath, "HPImageAssistant.exe"),
            Path.Combine(AppContext.BaseDirectory, "HPIA", "HPImageAssistant.exe"),
            Path.Combine(AppContext.BaseDirectory, "HPImageAssistant.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task WriteResultAsync(AgentJobResult result)
    {
        ValidateJobId(result.JobId);
        var resultPath = Path.Combine(ResultsPath, $"{result.JobId}.json");
        var tempPath = resultPath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(result, JsonOptions));
            File.Move(tempPath, resultPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }

        Log.Information("Wrote job result {JobId}: {Status}", result.JobId, result.Status);
    }

    private static void ValidateJobId(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 128 ||
            jobId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
        {
            throw new InvalidDataException("The job ID contains invalid characters.");
        }
    }

    private static void ClearDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);

        Directory.CreateDirectory(path);
    }

    /// <summary>
    /// Builds the /Offlinemode argument from the job payload.
    /// </summary>
    private static string GetOfflineModeArgument(AgentJob job)
    {
        if (!job.UseOfflineRepository || string.IsNullOrWhiteSpace(job.OfflineRepositoryPath))
            return "";

        var path = job.OfflineRepositoryPath.Trim();
        if (path.Contains('"'))
            throw new InvalidDataException("The offline repository path contains an invalid quote character.");

        return $" /Offlinemode:\"{path}\"";
    }
}

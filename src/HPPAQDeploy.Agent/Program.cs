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
            return mode switch
            {
                "watch" => await WatchAsync(),
                "run-once" => await RunOnceAsync(),
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

    private static async Task ProcessJobFileAsync(string jobFile)
    {
        AgentJob? job = null;
        try
        {
            job = JsonSerializer.Deserialize<AgentJob>(await File.ReadAllTextAsync(jobFile), JsonOptions)
                ?? throw new InvalidOperationException("Job file is empty or invalid.");

            var runningPath = Path.ChangeExtension(jobFile, ".running");
            File.Move(jobFile, runningPath, overwrite: true);

            Log.Information("Processing agent job {JobId} ({Type})", job.Id, job.Type);
            var result = job.Type switch
            {
                AgentJobType.Scan => await RunScanAsync(job),
                AgentJobType.Install => await RunInstallAsync(job),
                AgentJobType.Status => CreateStatusResult(job),
                _ => throw new InvalidOperationException($"Unsupported job type {job.Type}.")
            };

            await WriteResultAsync(result);
            File.Delete(runningPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to process job file {JobFile}", jobFile);
            if (job is not null)
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
        var args =
            "/Operation:Analyze /Category:All /Selection:All /Action:List " +
            "/Silent /Noninteractive /ReportFormat:JSON " +
            $"/ReportFolder:\"{ReportsPath}\" /Debug /LogFolder:\"{LogsPath}\"";

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
        ClearDirectory(ReportsPath);
        ClearDirectory(DownloadsPath);

        var hpiaExe = ResolveHpiaExe();
        var spListPath = Path.Combine(AgentRoot, "splist.txt");
        var numericIds = job.SoftPaqIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.StartsWith("sp", StringComparison.OrdinalIgnoreCase) ? id[2..] : id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        await File.WriteAllLinesAsync(spListPath, numericIds);

        var args =
            "/Operation:Analyze /Action:Install /Silent /Noninteractive " +
            $"/SoftpaqDownloadFolder:\"{DownloadsPath}\" /ReportFolder:\"{ReportsPath}\" " +
            $"/Debug /LogFolder:\"{LogsPath}\" /SPList:\"{spListPath}\"";

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
            try { process.Kill(entireProcessTree: true); } catch { }
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
        var resultPath = Path.Combine(ResultsPath, $"{result.JobId}.json");
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, JsonOptions));
        Log.Information("Wrote job result {JobId}: {Status}", result.JobId, result.Status);
    }

    private static void ClearDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);

        Directory.CreateDirectory(path);
    }
}

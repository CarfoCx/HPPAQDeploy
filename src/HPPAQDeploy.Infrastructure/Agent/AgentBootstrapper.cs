using System.Net;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Infrastructure.Remote;
using HPPAQDeploy.Shared.Configuration;
using HPPAQDeploy.Shared.Helpers;
using Serilog;

namespace HPPAQDeploy.Infrastructure.Agent;

public sealed class AgentBootstrapper : IAgentBootstrapper
{
    private const string RemoteRoot = @"C:\ProgramData\HPPAQDeploy";
    private const string RemoteAgentPath = RemoteRoot + @"\Agent";
    private const string RemoteHpiaPath = RemoteRoot + @"\HPIA";
    private const string RemoteRepositoryPath = RemoteRoot + @"\Repository";
    private const string AgentTaskName = "HPPAQDeployAgent";

    // Track which hosts have been bootstrapped this session to skip redundant copies
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _bootstrappedHosts
        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _stagedRepositoryVersions
        = new(StringComparer.OrdinalIgnoreCase);

    // Per-host locking to prevent concurrent bootstrapping to the same host
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _hostLocks
        = new(StringComparer.OrdinalIgnoreCase);

    // Circuit breaker: skip hosts that fail repeatedly to avoid wasting time
    private static readonly CircuitBreaker _circuitBreaker = new(
        failureThreshold: 3,
        openDuration: TimeSpan.FromMinutes(2),
        trackingWindow: TimeSpan.FromMinutes(10));

    private readonly IFileTransfer _fileTransfer;
    private readonly IRemoteExecutor _remoteExecutor;
    private readonly ILogger _logger = Log.ForContext<AgentBootstrapper>();

    public AgentBootstrapper(IFileTransfer fileTransfer, IRemoteExecutor remoteExecutor)
    {
        _fileTransfer = fileTransfer;
        _remoteExecutor = remoteExecutor;
    }

    public string RemoteOfflineRepositoryPath => RemoteRepositoryPath;

    public async Task BootstrapAsync(string hostname, NetworkCredential credential, CancellationToken ct)
    {
        // Circuit breaker: skip hosts that have failed too many times recently
        if (_circuitBreaker.IsOpen(hostname))
        {
            _logger.Warning("Circuit breaker is open for {Hostname}, skipping bootstrap (too many recent failures)", hostname);
            throw new InvalidOperationException(
                $"Bootstrap skipped for {hostname}: circuit breaker open after repeated failures. Will retry automatically in ~2 minutes.");
        }

        // Acquire per-host lock to prevent concurrent bootstrapping
        var hostLock = _hostLocks.GetOrAdd(hostname, _ => new SemaphoreSlim(1, 1));
        await hostLock.WaitAsync(ct);
        try
        {
            await BootstrapInternalAsync(hostname, credential, ct);
            _circuitBreaker.RecordSuccess(hostname);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _circuitBreaker.RecordFailure(hostname);
            throw;
        }
        finally
        {
            hostLock.Release();
        }
    }

    private async Task BootstrapInternalAsync(string hostname, NetworkCredential credential, CancellationToken ct)
    {
        var localAgentPath = ResolveLocalAgentPath();
        var localHpiaPath = AppSettings.HpiaExtractPath;
        var stageOfflineRepository = AppSettings.UseOfflineRepository;
        var repositorySharePath = AppSettings.RepositorySharePath?.Trim() ?? string.Empty;
        var stageLocalRepository = stageOfflineRepository &&
                                   string.IsNullOrWhiteSpace(repositorySharePath);
        var localRepositoryPath = AppSettings.RepositoryPath;

        if (!Directory.Exists(localAgentPath))
            throw new DirectoryNotFoundException($"Agent build output not found at {localAgentPath}.");

        if (!File.Exists(Path.Combine(localHpiaPath, "HPImageAssistant.exe")))
            throw new FileNotFoundException($"HPIA is not extracted at {localHpiaPath}. Extract HPIA before bootstrapping agents.");

        if (stageLocalRepository &&
            (!Directory.Exists(localRepositoryPath) || !Directory.EnumerateFileSystemEntries(localRepositoryPath).Any()))
        {
            throw new DirectoryNotFoundException(
                $"Offline repository is enabled, but the local repository is missing or empty at {localRepositoryPath}. Sync it in Settings before scanning.");
        }

        var repositoryVersion = stageLocalRepository
            ? GetRepositoryVersion(localRepositoryPath)
            : stageOfflineRepository
                ? $"share:{repositorySharePath}"
                : string.Empty;

        // Skip if already bootstrapped this session and recently (within 30 minutes)
        if (_bootstrappedHosts.TryGetValue(hostname, out var lastBootstrap) &&
            (DateTime.UtcNow - lastBootstrap).TotalMinutes < 30)
        {
            var repositoryCurrent = !stageOfflineRepository ||
                (_stagedRepositoryVersions.TryGetValue(hostname, out var stagedVersion) &&
                 string.Equals(stagedVersion, repositoryVersion, StringComparison.Ordinal));
            if (repositoryCurrent)
            {
                _logger.Information("Agent already bootstrapped on {Hostname} ({Elapsed:F0}m ago), skipping",
                    hostname, (DateTime.UtcNow - lastBootstrap).TotalMinutes);
                return;
            }

            _logger.Information("Offline repository changed for {Hostname}; refreshing bootstrap content", hostname);
        }

        _logger.Information("Bootstrapping HPPAQDeploy agent on {Hostname}", hostname);

        // Create required directories via UNC (much faster than remote WMI command execution)
        try
        {
            await using var remoteSession = await _fileTransfer
                .OpenAuthenticatedSessionAsync(hostname, credential, ct);
            var uncRoot = RemotePathHelper.ToUncPath(hostname, RemoteRoot);
            Directory.CreateDirectory(Path.Combine(uncRoot, "jobs"));
            Directory.CreateDirectory(Path.Combine(uncRoot, "results"));
            Directory.CreateDirectory(Path.Combine(uncRoot, "logs"));

            // Clean abandoned temp files and old results. Queued/running jobs belong
            // to the agent claim lifecycle and must never be deleted by bootstrap.
            CleanStaleJobFiles(hostname);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not create directories via UNC on {Hostname}, falling back to WMI", hostname);
            await _remoteExecutor.ExecuteAsync(
                hostname,
                credential,
                $"cmd /c mkdir \"{RemoteRoot}\" \"{RemoteRoot}\\jobs\" \"{RemoteRoot}\\results\" \"{RemoteRoot}\\logs\" 2>nul",
                null,
                TimeSpan.FromSeconds(30),
                ct);
        }

        // Smart copy: only copy files that are missing or outdated
        await SmartCopyAsync(hostname, credential, localAgentPath, RemoteAgentPath, ct);
        await SmartCopyAsync(hostname, credential, localHpiaPath, RemoteHpiaPath, ct);
        if (stageLocalRepository)
        {
            _logger.Information("Staging offline repository to {Hostname}:{RemotePath}", hostname, RemoteRepositoryPath);
            await _fileTransfer.CopyToRemoteAsync(
                hostname,
                credential,
                localRepositoryPath,
                RemoteRepositoryPath,
                ct);
            _stagedRepositoryVersions[hostname] = repositoryVersion;
        }
        else if (stageOfflineRepository)
        {
            await StageRepositoryShareAsync(hostname, credential, repositorySharePath, ct);
            _stagedRepositoryVersions[hostname] = repositoryVersion;
        }

        // Create the scheduled task (lightweight: schtasks command via UNC batch)
        await EnsureScheduledTaskAsync(hostname, credential, ct);

        _bootstrappedHosts[hostname] = DateTime.UtcNow;
        _logger.Information("HPPAQDeploy agent bootstrap completed on {Hostname}", hostname);
    }

    private static string GetRepositoryVersion(string repositoryPath)
    {
        var metadataPath = Path.Combine(repositoryPath, ".repository", "repository.json");
        FileSystemInfo versionSource = File.Exists(metadataPath)
            ? new FileInfo(metadataPath)
            : new DirectoryInfo(repositoryPath);

        return $"{versionSource.LastWriteTimeUtc.Ticks}:{(versionSource as FileInfo)?.Length ?? 0}";
    }

    /// <summary>
    /// Clean old job/result files from previous sessions to prevent stale state.
    /// </summary>
    private void CleanStaleJobFiles(string hostname)
    {
        try
        {
            var uncJobsPath = RemotePathHelper.ToUncPath(hostname, RemoteRoot + @"\jobs");
            var uncResultsPath = RemotePathHelper.ToUncPath(hostname, RemoteRoot + @"\results");

            CleanOldFiles(uncJobsPath, TimeSpan.FromHours(1), ".tmp");
            CleanOldFiles(uncResultsPath, TimeSpan.FromHours(24), ".json");
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not clean stale job files on {Hostname}", hostname);
        }
    }

    private static void CleanOldFiles(string directory, TimeSpan maxAge, string extension)
    {
        if (!Directory.Exists(directory)) return;

        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var file in Directory.GetFiles(directory))
        {
            try
            {
                if (file.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
                    File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Mirrors bootstrap content through the authenticated, cancellable SMB layer.
    /// Robocopy skips unchanged files, so a separate synchronous UNC probe would add
    /// latency and can hang when the endpoint is unavailable.
    /// </summary>
    private async Task SmartCopyAsync(
        string hostname,
        NetworkCredential credential,
        string localPath,
        string remotePath,
        CancellationToken ct)
    {
        await _fileTransfer.CopyToRemoteAsync(hostname, credential, localPath, remotePath, ct);
    }

    private async Task StageRepositoryShareAsync(
        string hostname,
        NetworkCredential credential,
        string repositorySharePath,
        CancellationToken ct)
    {
        if (!repositorySharePath.StartsWith(@"\\", StringComparison.Ordinal) ||
            repositorySharePath.IndexOfAny(['"', '\r', '\n', '%']) >= 0)
        {
            throw new InvalidOperationException(
                "The repository share must be a valid UNC path and cannot contain quotes, percent signs, or line breaks.");
        }

        var sourcePath = repositorySharePath.TrimEnd('\\');
        _logger.Information(
            "Mirroring repository share {RepositoryShare} to {Hostname}:{RemotePath}",
            sourcePath,
            hostname,
            RemoteRepositoryPath);

        var result = await _remoteExecutor.ExecuteAsync(
            hostname,
            credential,
            $"robocopy \"{sourcePath}\" \"{RemoteRepositoryPath}\" /MIR /MT:8 /R:1 /W:2 /NP /NFL /NDL",
            null,
            TimeSpan.FromMinutes(AppSettings.FileTransferTimeoutMinutes),
            ct);

        // Robocopy uses 0-7 for successful outcomes and 8+ for failures.
        if (result.ExitCode is < 0 or >= 8)
        {
            throw new InvalidOperationException(
                $"Repository staging failed on {hostname} with robocopy exit code {result.ExitCode}. {result.ErrorOutput}".Trim());
        }
    }

    private async Task EnsureScheduledTaskAsync(string hostname, NetworkCredential credential, CancellationToken ct)
    {
        // Clean up any existing service/task first, then create a fresh one
        // Use a batch file via UNC to avoid heavyweight WMI execution for simple commands
        try
        {
            var batchContent =
                "@echo off\r\n" +
                "mkdir \"%SystemRoot%\\System32\\config\\systemprofile\\Desktop\" >nul 2>&1\r\n" +
                "mkdir \"%SystemRoot%\\SysWOW64\\config\\systemprofile\\Desktop\" >nul 2>&1\r\n" +
                $"sc stop \"{AgentTaskName}\" >nul 2>&1\r\n" +
                $"sc delete \"{AgentTaskName}\" >nul 2>&1\r\n" +
                $"schtasks /Create /TN \"{AgentTaskName}\" /TR \"{RemoteAgentPath}\\HPPAQDeploy.Agent.exe run-once\" /SC ONSTART /RU SYSTEM /RL HIGHEST /F\r\n";

            var uncBatchPath = RemotePathHelper.ToUncPath(hostname, RemoteRoot + @"\setup_task.bat");
            await using (var remoteSession = await _fileTransfer
                .OpenAuthenticatedSessionAsync(hostname, credential, ct))
            {
                await File.WriteAllTextAsync(uncBatchPath, batchContent, ct);
            }

            // Execute the batch via WMI
            await _remoteExecutor.ExecuteAsync(
                hostname,
                credential,
                $"cmd /c \"{RemoteRoot}\\setup_task.bat\"",
                null,
                TimeSpan.FromSeconds(30),
                ct);

            // Clean up the batch file
            try
            {
                await using var remoteSession = await _fileTransfer
                    .OpenAuthenticatedSessionAsync(hostname, credential, ct);
                File.Delete(uncBatchPath);
            }
            catch { }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Batch-based task setup failed on {Hostname}, falling back to individual commands", hostname);

            // Fallback: original approach
            await _remoteExecutor.ExecuteAsync(
                hostname,
                credential,
                $"cmd /c sc stop \"{AgentTaskName}\" >nul 2>&1 & sc delete \"{AgentTaskName}\" >nul 2>&1 & exit /b 0",
                null,
                TimeSpan.FromSeconds(30),
                ct);

            var agentExe = RemoteAgentPath + @"\HPPAQDeploy.Agent.exe";
            var createTaskCommand =
                $"cmd /c schtasks /Create /TN \"{AgentTaskName}\" " +
                $"/TR \"{agentExe} run-once\" /SC ONSTART /RU SYSTEM /RL HIGHEST /F";

            await _remoteExecutor.ExecuteAsync(
                hostname,
                credential,
                createTaskCommand,
                null,
                TimeSpan.FromSeconds(30),
                ct);
        }
    }

    public async Task RunOnceAsync(string hostname, NetworkCredential credential, CancellationToken ct)
    {
        _logger.Information("Starting HPPAQDeploy agent task on {Hostname}", hostname);

        await _remoteExecutor.ExecuteAsync(
            hostname,
            credential,
            $"cmd /c schtasks /Run /TN \"{AgentTaskName}\"",
            null,
            TimeSpan.FromSeconds(30),
            ct);
    }

    /// <summary>
    /// Invalidates the bootstrap cache for a specific host, forcing re-bootstrap next time.
    /// </summary>
    public static void InvalidateCache(string hostname)
    {
        _bootstrappedHosts.TryRemove(hostname, out _);
    }

    /// <summary>
    /// Invalidates all bootstrap caches.
    /// </summary>
    public static void InvalidateAllCaches()
    {
        _bootstrappedHosts.Clear();
    }

    private static string ResolveLocalAgentPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Agent"),
            Path.Combine(AppContext.BaseDirectory, "..", "src", "HPPAQDeploy.Agent", "bin", "Release", "net8.0-windows", "win-x64", "publish"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "src", "HPPAQDeploy.Agent", "bin", "Release", "net8.0-windows", "win-x64", "publish"),
            Path.Combine(AppContext.BaseDirectory, "..", "src", "HPPAQDeploy.Agent", "bin", "Debug", "net8.0-windows"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "src", "HPPAQDeploy.Agent", "bin", "Debug", "net8.0-windows")
        };

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "HPPAQDeploy.Agent.exe")))
            ?? Path.GetFullPath(candidates[0]);
    }
}

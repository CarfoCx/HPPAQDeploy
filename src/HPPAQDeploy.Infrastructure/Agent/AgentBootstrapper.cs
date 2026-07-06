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
    private const string AgentTaskName = "HPPAQDeployAgent";

    // Track which hosts have been bootstrapped this session to skip redundant copies
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _bootstrappedHosts
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

        if (!Directory.Exists(localAgentPath))
            throw new DirectoryNotFoundException($"Agent build output not found at {localAgentPath}.");

        if (!File.Exists(Path.Combine(localHpiaPath, "HPImageAssistant.exe")))
            throw new FileNotFoundException($"HPIA is not extracted at {localHpiaPath}. Extract HPIA before bootstrapping agents.");

        // Skip if already bootstrapped this session and recently (within 30 minutes)
        if (_bootstrappedHosts.TryGetValue(hostname, out var lastBootstrap) &&
            (DateTime.UtcNow - lastBootstrap).TotalMinutes < 30)
        {
            // Verify agent is still present via quick UNC check
            var uncAgentExe = RemotePathHelper.ToUncPath(hostname, RemoteAgentPath + @"\HPPAQDeploy.Agent.exe");
            var uncHpiaExe = RemotePathHelper.ToUncPath(hostname, RemoteHpiaPath + @"\HPImageAssistant.exe");
            try
            {
                if (File.Exists(uncAgentExe) && File.Exists(uncHpiaExe))
                {
                    _logger.Information("Agent already bootstrapped on {Hostname} ({Elapsed:F0}m ago), skipping",
                        hostname, (DateTime.UtcNow - lastBootstrap).TotalMinutes);
                    return;
                }
                _logger.Information("Agent files missing on {Hostname} despite recent bootstrap, re-bootstrapping", hostname);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "UNC pre-check failed for {Hostname}, proceeding with bootstrap", hostname);
            }
        }

        _logger.Information("Bootstrapping HPPAQDeploy agent on {Hostname}", hostname);

        // Create required directories via UNC (much faster than remote WMI command execution)
        try
        {
            var uncRoot = RemotePathHelper.ToUncPath(hostname, RemoteRoot);
            Directory.CreateDirectory(Path.Combine(uncRoot, "jobs"));
            Directory.CreateDirectory(Path.Combine(uncRoot, "results"));
            Directory.CreateDirectory(Path.Combine(uncRoot, "logs"));
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

        // Clean stale job/result files that may interfere with new scans
        CleanStaleJobFiles(hostname);

        // Smart copy: only copy files that are missing or outdated
        await SmartCopyAsync(hostname, credential, localAgentPath, RemoteAgentPath, ct);
        await SmartCopyAsync(hostname, credential, localHpiaPath, RemoteHpiaPath, ct);

        // Create the scheduled task (lightweight: schtasks command via UNC batch)
        await EnsureScheduledTaskAsync(hostname, credential, ct);

        _bootstrappedHosts[hostname] = DateTime.UtcNow;
        _logger.Information("HPPAQDeploy agent bootstrap completed on {Hostname}", hostname);
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

            CleanOldFiles(uncJobsPath, TimeSpan.FromHours(1));
            CleanOldFiles(uncResultsPath, TimeSpan.FromHours(1));
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not clean stale job files on {Hostname}", hostname);
        }
    }

    private static void CleanOldFiles(string directory, TimeSpan maxAge)
    {
        if (!Directory.Exists(directory)) return;

        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var file in Directory.GetFiles(directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Smart copy that only transfers files that are missing or have different sizes.
    /// Much faster than blindly copying everything every time.
    /// </summary>
    private async Task SmartCopyAsync(
        string hostname,
        NetworkCredential credential,
        string localPath,
        string remotePath,
        CancellationToken ct)
    {
        var uncRemotePath = RemotePathHelper.ToUncPath(hostname, remotePath);

        // Check if key files already exist and match
        bool needsCopy = true;
        try
        {
            if (Directory.Exists(uncRemotePath))
            {
                var localFiles = Directory.GetFiles(localPath, "*", SearchOption.AllDirectories);
                var missingOrDifferent = 0;
                var checkedCount = 0;
                
                // Check a sample of files (exe/dll) for existence and size match
                foreach (var localFile in localFiles.Where(f =>
                    f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                {
                    ct.ThrowIfCancellationRequested();
                    var relativePath = Path.GetRelativePath(localPath, localFile);
                    var remoteFile = Path.Combine(uncRemotePath, relativePath);

                    if (!File.Exists(remoteFile) || new FileInfo(remoteFile).Length != new FileInfo(localFile).Length)
                    {
                        missingOrDifferent++;
                        if (missingOrDifferent > 3) break; // Too many differences, just recopy everything
                    }
                    checkedCount++;
                }

                needsCopy = missingOrDifferent > 0 || checkedCount == 0;
                if (!needsCopy)
                    _logger.Information("Files on {Hostname} at {Path} are up-to-date, skipping copy", hostname, remotePath);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Smart copy pre-check failed for {Hostname}, will do full copy", hostname);
        }

        if (needsCopy)
        {
            await _fileTransfer.CopyToRemoteAsync(hostname, credential, localPath, remotePath, ct);
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
            await File.WriteAllTextAsync(uncBatchPath, batchContent, ct);

            // Execute the batch via WMI
            await _remoteExecutor.ExecuteAsync(
                hostname,
                credential,
                $"cmd /c \"{RemoteRoot}\\setup_task.bat\"",
                null,
                TimeSpan.FromSeconds(30),
                ct);

            // Clean up the batch file
            try { File.Delete(uncBatchPath); } catch { }
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

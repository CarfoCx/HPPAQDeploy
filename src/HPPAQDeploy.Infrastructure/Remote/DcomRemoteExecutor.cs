using System.Management;
using System.Net;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Shared.Configuration;
using HPPAQDeploy.Shared.Helpers;
using Serilog;

namespace HPPAQDeploy.Infrastructure.Remote;

/// <summary>
/// Implements IRemoteExecutor using WMI Win32_Process.Create over DCOM.
/// Polls for process completion and retrieves exit code via a temp file.
/// </summary>
public class DcomRemoteExecutor : IRemoteExecutor
{
    private readonly ILogger _logger = Log.ForContext<DcomRemoteExecutor>();
    private readonly CircuitBreaker _circuitBreaker;

    public DcomRemoteExecutor(CircuitBreaker circuitBreaker)
    {
        _circuitBreaker = circuitBreaker;
    }

    public async Task<RemoteProcessResult> ExecuteAsync(
        string hostname,
        NetworkCredential cred,
        string commandLine,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken ct,
        IProgress<string>? progress = null)
    {
        if (_circuitBreaker.IsOpen(hostname))
        {
            _logger.Warning("Circuit breaker open for {Hostname}, skipping execution", hostname);
            return new RemoteProcessResult(-1, string.Empty, $"Host {hostname} circuit breaker is open (too many recent failures). Will retry automatically.");
        }

        try
        {
            var result = await RetryHelper.RetryAsync(async () =>
            {
                return await ExecuteInternalAsync(hostname, cred, commandLine, workingDirectory, timeout, ct, progress)
                    .ConfigureAwait(false);
            }, ct: ct).ConfigureAwait(false);

            _circuitBreaker.RecordSuccess(hostname);
            return result;
        }
        catch (Exception)
        {
            _circuitBreaker.RecordFailure(hostname);
            throw;
        }
    }

    /// <summary>
    /// Validates that a hostname/IP does not contain characters that could be used for injection.
    /// </summary>
    private static void ValidateHostname(string hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            throw new ArgumentException("Hostname cannot be null or empty.", nameof(hostname));

        // Allow only alphanumeric, dots, hyphens, and underscores (valid DNS/NetBIOS chars)
        foreach (var c in hostname)
        {
            if (!char.IsLetterOrDigit(c) && c != '.' && c != '-' && c != '_')
                throw new ArgumentException(
                    $"Hostname contains invalid character '{c}'. Only letters, digits, dots, hyphens, and underscores are allowed.",
                    nameof(hostname));
        }
    }

    private async Task<RemoteProcessResult> ExecuteInternalAsync(
        string hostname,
        NetworkCredential cred,
        string commandLine,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken ct,
        IProgress<string>? progress = null)
    {
        ValidateHostname(hostname);

        using var _ = Serilog.Context.LogContext.PushProperty("TargetHost", hostname);
        _logger.Information("Executing remote command on {Hostname}: {Command}", hostname, commandLine);

        // ManagementScope does NOT implement IDisposable in System.Management, so no using/dispose needed.
        var scope = await Task.Run(() => WmiConnectionFactory.CreateScope(hostname, cred), ct)
            .ConfigureAwait(false);

        // Wrap command to capture exit code to a temp file.
        // Use a batch file approach to avoid nested quoting issues with cmd /c.
        var remoteTempPath = AppSettings.RemoteTempPath;
        string exitCodeFile = Path.Combine(remoteTempPath, "exitcode.txt");
        string batchFile = Path.Combine(remoteTempPath, "run_hpia.bat");

        // Build batch file content — pre-map network shares if command uses UNC paths
        var batchLines = new System.Text.StringBuilder();
        batchLines.Append("@echo off\r\n");

        // Set working directory to HPIA folder so it can find its DLLs
        batchLines.Append($"cd /d \"{remoteTempPath}\"\r\n");

        // If the command references a UNC share for OfflineMode, map it first with user credentials
        if (!string.IsNullOrEmpty(AppSettings.RepositorySharePath) &&
            commandLine.Contains(AppSettings.RepositorySharePath, StringComparison.OrdinalIgnoreCase))
        {
            var sharePath = AppSettings.RepositorySharePath;
            var domain = cred.Domain;
            var user = cred.UserName;
            var pass = cred.Password;
            var qualifiedUser = !string.IsNullOrEmpty(domain) ? $"{domain}\\{user}" : user;
            batchLines.Append($"net use \"{sharePath}\" /user:\"{qualifiedUser}\" \"{pass}\" >nul 2>&1\r\n");
        }

        batchLines.Append($"{commandLine}\r\n");
        batchLines.Append($"echo %ERRORLEVEL% > \"{exitCodeFile}\"\r\n");

        // Disconnect the share after HPIA finishes (non-critical)
        if (!string.IsNullOrEmpty(AppSettings.RepositorySharePath) &&
            commandLine.Contains(AppSettings.RepositorySharePath, StringComparison.OrdinalIgnoreCase))
        {
            batchLines.Append($"net use \"{AppSettings.RepositorySharePath}\" /delete >nul 2>&1\r\n");
        }

        string batchContent = batchLines.ToString();
        
        bool useBatchFile = false;
        try
        {
            var uncBatchPath = ConvertToUncPath(hostname, batchFile);
            var uncDir = Path.GetDirectoryName(uncBatchPath)!;
            Directory.CreateDirectory(uncDir);
            File.WriteAllText(uncBatchPath, batchContent);
            useBatchFile = true;
            _logger.Debug("Created batch file on {Hostname} at {Path}", hostname, batchFile);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not write batch file to {Hostname}, falling back to cmd /c", hostname);
        }

        string wrappedCommand;
        if (useBatchFile)
        {
            wrappedCommand = $"cmd /c \"{batchFile}\"";
        }
        else
        {
            // Fallback: escape inner quotes for cmd /c
            var escapedCmd = commandLine.Replace("\"", "\\\"");
            wrappedCommand = $"cmd /c \"{escapedCmd} & echo %ERRORLEVEL% > {exitCodeFile}\"";
        }

        // Use a scheduled task to run the command in a proper user session.
        // Win32_Process.Create runs in Session 0 which can cause HPIA to hang.
        var taskName = $"HPPAQDeploy_{Guid.NewGuid():N}";
        var taskUser = !string.IsNullOrEmpty(cred.Domain) ? $"{cred.Domain}\\{cred.UserName}" : cred.UserName;

        // Create a VBS wrapper to run the batch file completely hidden (no cmd window or HPIA window)
        var vbsFile = Path.Combine(remoteTempPath, "run_hidden.vbs");
        var vbsContent =
            "Set WshShell = CreateObject(\"WScript.Shell\")\r\n" +
            $"WshShell.Run \"cmd /c \"\"\" & \"{batchFile}\" & \"\"\"\", 0, True\r\n";
        try
        {
            var uncVbsPath = ConvertToUncPath(hostname, vbsFile);
            File.WriteAllText(uncVbsPath, vbsContent);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not write VBS wrapper to {Hostname}, cmd window may be visible", hostname);
        }

        // Create and run a one-time scheduled task via Win32_Process.Create
        // /it = interactive (run in logged-in user's desktop session — required for HPIA's WPF components)
        var taskCommand = File.Exists(ConvertToUncPath(hostname, vbsFile))
            ? $"wscript.exe \\\"{vbsFile}\\\""
            : $"cmd /c \\\"{batchFile}\\\"";
        // /it = interactive token: required for HPIA's WPF components.
        // The VBS wrapper hides the cmd window; HPIA's /Silent flag suppresses its own UI.
        var schtasksCreate = $"schtasks /create /tn \"{taskName}\" /tr \"{taskCommand}\" " +
            $"/sc once /st 00:00 /f /ru \"{taskUser}\" /rp \"{cred.Password}\" /rl highest /it";
        var schtasksRun = $"schtasks /run /tn \"{taskName}\"";

        // Create the scheduled task
        var (createRv, _) = await Task.Run(() =>
        {
            using var processClass = new ManagementClass(scope, new ManagementPath("Win32_Process"), null);
            var inParams = processClass.GetMethodParameters("Create");
            inParams["CommandLine"] = $"cmd /c {schtasksCreate}";
            var outParams = processClass.InvokeMethod("Create", inParams, null);
            uint rv = (uint)outParams["ReturnValue"];
            uint p = rv == 0 ? (uint)outParams["ProcessId"] : 0;
            return (rv, p);
        }, ct).ConfigureAwait(false);

        if (createRv != 0)
        {
            _logger.Error("Failed to create scheduled task on {Hostname}, return value {Rv}", hostname, createRv);
            return new RemoteProcessResult((int)createRv, string.Empty, "Failed to create scheduled task");
        }

        // Wait for schtasks /create to finish
        await Task.Delay(2000, ct).ConfigureAwait(false);

        // Run the scheduled task
        var (runRv, _2) = await Task.Run(() =>
        {
            using var processClass = new ManagementClass(scope, new ManagementPath("Win32_Process"), null);
            var inParams = processClass.GetMethodParameters("Create");
            inParams["CommandLine"] = $"cmd /c {schtasksRun}";
            var outParams = processClass.InvokeMethod("Create", inParams, null);
            uint rv = (uint)outParams["ReturnValue"];
            uint p = rv == 0 ? (uint)outParams["ProcessId"] : 0;
            return (rv, p);
        }, ct).ConfigureAwait(false);

        // Wait for the task to start and find the batch process PID
        await Task.Delay(2000, ct).ConfigureAwait(false);

        // Find the PID of the cmd.exe running our batch file
        uint pid = 0;
        var returnValue = runRv;
        try
        {
            pid = await Task.Run(() =>
            {
                var query = new ObjectQuery(
                    $"SELECT ProcessId FROM Win32_Process WHERE CommandLine LIKE '%{taskName}%' OR CommandLine LIKE '%run_hpia.bat%'");
                using var searcher = new ManagementObjectSearcher(scope, query);
                var results = searcher.Get();
                foreach (ManagementObject obj in results)
                {
                    return (uint)obj["ProcessId"];
                }
                return (uint)0;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Debug("Could not find batch process PID on {Hostname}, will poll by exit code file instead: {Error}", hostname, ex.Message); }

        // If we couldn't find the PID, poll by checking for the exit code file instead
        _logger.Information("Scheduled task {TaskName} started on {Hostname}, tracking PID {Pid}", taskName, hostname, pid);

        progress?.Report($"Scheduled task started on {hostname}");

        // Poll for completion by checking if the exit code file appears (batch finished)
        var uncExitCodePath = ConvertToUncPath(hostname, exitCodeFile);
        // Delete any stale exit code file first
        try { if (File.Exists(uncExitCodePath)) File.Delete(uncExitCodePath); } catch (Exception ex) { Log.Debug(ex, "Failed to delete stale exit code file on {Hostname}", hostname); }

        // Monitor HPIA Downloads and Reports directories for real progress
        var uncDownloadsDir = ConvertToUncPath(hostname, Path.Combine(AppSettings.RemoteTempPath, "Downloads"));
        var uncReportsDir = ConvertToUncPath(hostname, Path.Combine(AppSettings.RemoteTempPath, "Reports"));
        int _lastSoftPaqCount = 0;
        string _lastSoftPaqName = "";
        string _lastProgressMessage = "";

        bool processRunning = true;
        bool waitingReported = false;
        var startTime = DateTime.UtcNow;
        var lastLogTime = DateTime.UtcNow;
        var lastProgressTime = DateTime.UtcNow;

        // Create a linked token that fires on either user cancellation or timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var linkedCt = timeoutCts.Token;

        try
        {
        while (processRunning)
        {
            try
            {
                linkedCt.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout expired (not user cancellation)
                _logger.Warning("Remote process on {Hostname} timed out after {Timeout}", hostname, timeout);
                progress?.Report($"Remote process on {hostname} timed out after {timeout.TotalMinutes:F0} minutes");
                if (pid > 0)
                    try { await Task.Run(() => TryKillProcess(scope, pid)).ConfigureAwait(false); } catch (Exception ex) { Log.Debug(ex, "Failed to kill process PID {Pid} on {Hostname} during timeout", pid, hostname); }
                // Kill any HPImageAssistant.exe still running
                try { await Task.Run(() => TryKillProcessByName(scope, "HPImageAssistant")).ConfigureAwait(false); } catch (Exception ex) { Log.Debug(ex, "Failed to kill HPImageAssistant on {Hostname} during timeout", hostname); }
                CleanupScheduledTask(scope, taskName);
                return new RemoteProcessResult(-1, string.Empty, "Process timed out");
            }

            await Task.Delay(3000, linkedCt).ConfigureAwait(false);

            var elapsed = DateTime.UtcNow - startTime;

            // Report progress to UI every ~10 seconds, but only when something changes
            if (DateTime.UtcNow - lastProgressTime > TimeSpan.FromSeconds(10))
            {
                var progressDetail = TryGetSoftPaqProgress(uncDownloadsDir, uncReportsDir,
                    ref _lastSoftPaqCount, ref _lastSoftPaqName);

                if (!string.IsNullOrEmpty(progressDetail))
                {
                    var msg = $"{hostname}: {progressDetail}";
                    if (msg != _lastProgressMessage)
                    {
                        progress?.Report(msg);
                        _lastProgressMessage = msg;
                    }
                }
                else if (!waitingReported)
                {
                    progress?.Report($"{hostname}: Waiting for HPIA...");
                    waitingReported = true;
                }

                lastProgressTime = DateTime.UtcNow;
            }

            // Log to file every ~60 seconds (less spam)
            if (DateTime.UtcNow - lastLogTime > TimeSpan.FromSeconds(60))
            {
                var logDetail = !string.IsNullOrEmpty(_lastSoftPaqName)
                    ? $"Current: {_lastSoftPaqName} ({_lastSoftPaqCount} SoftPaqs processed)"
                    : $"{elapsed.TotalSeconds:F0}s elapsed";
                _logger.Information("Remote process on {Hostname}: {Detail}", hostname, logDetail);
                lastLogTime = DateTime.UtcNow;
            }

            // Check if the exit code file has appeared (means the batch completed)
            try
            {
                processRunning = !File.Exists(uncExitCodePath);
            }
            catch
            {
                // If we can't check the file, fall back to PID-based polling
                if (pid > 0)
                {
                    try
                    {
                        processRunning = await Task.Run(() =>
                        {
                            var query = new ObjectQuery($"SELECT ProcessId FROM Win32_Process WHERE ProcessId = {pid}");
                            using var searcher = new ManagementObjectSearcher(scope, query);
                            return searcher.Get().Count > 0;
                        }, linkedCt).ConfigureAwait(false);
                    }
                    catch (ManagementException ex)
                    {
                        _logger.Warning(ex, "Error polling on {Hostname}", hostname);
                        processRunning = false;
                    }
                }
            }
        }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout fired via the linked token during Task.Delay or polling
            _logger.Warning("Remote process on {Hostname} timed out after {Timeout}", hostname, timeout);
            progress?.Report($"Remote process on {hostname} timed out after {timeout.TotalMinutes:F0} minutes");
            if (pid > 0)
                try { await Task.Run(() => TryKillProcess(scope, pid)).ConfigureAwait(false); } catch (Exception ex) { Log.Debug(ex, "Failed to kill process PID {Pid} on {Hostname} during timeout cleanup", pid, hostname); }
            try { await Task.Run(() => TryKillProcessByName(scope, "HPImageAssistant")).ConfigureAwait(false); } catch (Exception ex) { Log.Debug(ex, "Failed to kill HPImageAssistant on {Hostname} during timeout cleanup", hostname); }
            CleanupScheduledTask(scope, taskName);
            return new RemoteProcessResult(-1, string.Empty, "Process timed out");
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("Scan cancelled — killing remote processes on {Hostname}", hostname);
            if (pid > 0)
                try { await Task.Run(() => TryKillProcess(scope, pid)).ConfigureAwait(false); } catch (Exception ex) { Log.Debug(ex, "Failed to kill process PID {Pid} on {Hostname} during cancellation cleanup", pid, hostname); }
            try { await Task.Run(() => TryKillProcessByName(scope, "HPImageAssistant")).ConfigureAwait(false); } catch (Exception ex) { Log.Debug(ex, "Failed to kill HPImageAssistant on {Hostname} during cancellation cleanup", hostname); }
            CleanupScheduledTask(scope, taskName);
            throw;
        }

        _logger.Information("Remote process completed on {Hostname}", hostname);

        // Small delay to let file system settle
        await Task.Delay(500, ct).ConfigureAwait(false);

        // Read exit code from the temp file via SMB
        int exitCode = ReadRemoteExitCode(hostname, cred, exitCodeFile);

        // Clean up batch file and scheduled task
        if (useBatchFile)
        {
            try
            {
                var uncBatchPath = ConvertToUncPath(hostname, batchFile);
                if (File.Exists(uncBatchPath)) File.Delete(uncBatchPath);
                var uncVbsPath = ConvertToUncPath(hostname, vbsFile);
                if (File.Exists(uncVbsPath)) File.Delete(uncVbsPath);
            }
            catch (Exception ex) { Log.Debug(ex, "Failed to clean up batch/VBS files on {Hostname}", hostname); }
        }
        CleanupScheduledTask(scope, taskName);

        return new RemoteProcessResult(exitCode, string.Empty, string.Empty);
    }

    public async Task<bool> TestConnectionAsync(
        string hostname,
        NetworkCredential credential,
        CancellationToken ct)
    {
        ValidateHostname(hostname);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            return await Task.Run(() =>
            {
                var scope = WmiConnectionFactory.CreateScope(hostname, credential);
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT Caption FROM Win32_OperatingSystem"));
                var results = searcher.Get();
                return results.Count > 0;
            }, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.Warning("WMI connection test to {Hostname} timed out", hostname);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "WMI connection test to {Hostname} failed", hostname);
            return false;
        }
    }

    private int ReadRemoteExitCode(string hostname, NetworkCredential cred, string remoteFilePath)
    {
        try
        {
            // Convert remote local path to UNC path
            string uncPath = ConvertToUncPath(hostname, remoteFilePath);

            if (File.Exists(uncPath))
            {
                var content = File.ReadAllText(uncPath).Trim();
                if (int.TryParse(content, out int exitCode))
                {
                    return exitCode;
                }

                _logger.Warning("Could not parse exit code from file content: '{Content}'", content);
            }
            else
            {
                _logger.Warning("Exit code file not found at {UncPath}", uncPath);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to read exit code file from {Hostname}", hostname);
        }

        return -1;
    }

    private static string ConvertToUncPath(string hostname, string remotePath)
    {
        // C:\path -> \\hostname\C$\path
        if (remotePath.Length >= 2 && remotePath[1] == ':')
        {
            var driveLetter = remotePath[0];
            var remainingPath = remotePath.Substring(2);
            return $"\\\\{hostname}\\{driveLetter}${remainingPath}";
        }

        return $"\\\\{hostname}\\{remotePath}";
    }

    private void TryKillProcess(ManagementScope scope, uint pid)
    {
        try
        {
            var query = new ObjectQuery($"SELECT * FROM Win32_Process WHERE ProcessId = {pid}");
            using var searcher = new ManagementObjectSearcher(scope, query);
            foreach (ManagementObject process in searcher.Get())
            {
                process.InvokeMethod("Terminate", new object[] { (uint)1 });
                _logger.Information("Killed remote process PID {Pid}", pid);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to kill remote process PID {Pid}", pid);
        }
    }

    private void TryKillProcessByName(ManagementScope scope, string processName)
    {
        try
        {
            var query = new ObjectQuery($"SELECT * FROM Win32_Process WHERE Name LIKE '{processName}%'");
            using var searcher = new ManagementObjectSearcher(scope, query);
            foreach (ManagementObject process in searcher.Get())
            {
                var pid = (uint)process["ProcessId"];
                process.InvokeMethod("Terminate", new object[] { (uint)1 });
                _logger.Information("Killed remote process {Name} PID {Pid}", processName, pid);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to kill remote processes named {Name}", processName);
        }
    }

    /// <summary>
    /// Monitors the remote HPIA Downloads directory to track which SoftPaqs are being processed.
    /// Each SoftPaq gets its own subfolder (e.g., sp165857_IntelGraphicsDriver/) when downloaded/installed.
    /// </summary>
    private string TryGetSoftPaqProgress(string uncDownloadsDir, string uncReportsDir,
        ref int lastCount, ref string lastName)
    {
        try
        {
            // Check Downloads directory for SoftPaq folders
            if (Directory.Exists(uncDownloadsDir))
            {
                var spFolders = Directory.GetDirectories(uncDownloadsDir)
                    .Select(d => Path.GetFileName(d))
                    .Where(n => n.StartsWith("sp", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (spFolders.Count > 0)
                {
                    lastCount = spFolders.Count;

                    // Find the most recently modified folder (currently being worked on)
                    var currentFolder = Directory.GetDirectories(uncDownloadsDir)
                        .Where(d => Path.GetFileName(d).StartsWith("sp", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(d => new DirectoryInfo(d).LastWriteTimeUtc)
                        .FirstOrDefault();

                    if (currentFolder != null)
                    {
                        var folderName = Path.GetFileName(currentFolder);
                        // Parse friendly name: "sp165857_IntelGraphicsDriverandControlPanel" -> "Intel Graphics Driver and Control Panel"
                        var parts = folderName.Split('_', 2);
                        var spId = parts[0];
                        var friendlyName = parts.Length > 1
                            ? System.Text.RegularExpressions.Regex.Replace(parts[1], "([a-z])([A-Z])", "$1 $2")
                            : spId;
                        lastName = $"{spId} {friendlyName}";

                        return $"Processing {spId} ({lastCount} so far) — {friendlyName}";
                    }

                    return $"Processing SoftPaqs ({lastCount} so far)";
                }
            }

            // Check if Reports directory has appeared (means analysis phase is done)
            if (Directory.Exists(uncReportsDir))
            {
                var reportFiles = Directory.GetFiles(uncReportsDir, "*.json", SearchOption.AllDirectories);
                if (reportFiles.Length > 0 && lastCount == 0)
                    return "Analysis complete, generating report...";
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Non-critical error during SoftPaq progress monitoring");
        }

        return lastCount > 0 ? $"Processing SoftPaqs ({lastCount} so far)" : "";
    }

    private void CleanupScheduledTask(ManagementScope scope, string taskName)
    {
        try
        {
            using var processClass = new ManagementClass(scope, new ManagementPath("Win32_Process"), null);
            var inParams = processClass.GetMethodParameters("Create");
            inParams["CommandLine"] = $"cmd /c schtasks /delete /tn \"{taskName}\" /f";
            processClass.InvokeMethod("Create", inParams, null);
            _logger.Debug("Cleaned up scheduled task {TaskName}", taskName);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to clean up scheduled task {TaskName}", taskName);
        }
    }
}

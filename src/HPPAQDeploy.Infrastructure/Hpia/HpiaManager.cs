using System.Net;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Shared.Configuration;
using HPPAQDeploy.Shared.Helpers;
using Serilog;

namespace HPPAQDeploy.Infrastructure.Hpia;

/// <summary>
/// Implements IHpiaManager. Central orchestrator for HPIA extraction, staging,
/// analysis, deployment, and cleanup on remote machines.
/// Uses offline repository mode so remote machines don't need internet access.
/// </summary>
public class HpiaManager : IHpiaManager
{
    private readonly HpiaExtractor _extractor;
    private readonly HpiaReportParser _reportParser;
    private readonly IFileTransfer _fileTransfer;
    private readonly IRemoteExecutor _remoteExecutor;
    private readonly ILogger _logger = Log.ForContext<HpiaManager>();

    private static string RemoteHpiaPath => AppSettings.RemoteTempPath;
    private static string RemoteRepoPath => !string.IsNullOrEmpty(AppSettings.RepositorySharePath)
        ? AppSettings.RepositorySharePath
        : Path.Combine(RemoteHpiaPath, "Repository");
    private static string RemoteReportsPath => Path.Combine(RemoteHpiaPath, "Reports");
    private static string RemoteDownloadsPath => Path.Combine(RemoteHpiaPath, "Downloads");
    private static string RemoteHpiaExe => Path.Combine(RemoteHpiaPath, "HPImageAssistant.exe");
    private const string RemoteBiosPasswordEnvVar = "HPPAQDEPLOY_BIOSPWD";

    // Track which hosts have been staged this session to avoid redundant copies (thread-safe)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _stagedHosts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _repoStagedHosts = new(StringComparer.OrdinalIgnoreCase);

    // Per-host locking to prevent TOCTOU races when multiple tasks stage to the same host concurrently
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _hostLocks = new(StringComparer.OrdinalIgnoreCase);

    public HpiaManager(
        HpiaExtractor extractor,
        HpiaReportParser reportParser,
        IFileTransfer fileTransfer,
        IRemoteExecutor remoteExecutor)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _reportParser = reportParser ?? throw new ArgumentNullException(nameof(reportParser));
        _fileTransfer = fileTransfer ?? throw new ArgumentNullException(nameof(fileTransfer));
        _remoteExecutor = remoteExecutor ?? throw new ArgumentNullException(nameof(remoteExecutor));
    }

    public async Task ExtractLocallyAsync(CancellationToken ct)
    {
        _logger.Information("Extracting HPIA locally");
        await _extractor.ExtractAsync(ct).ConfigureAwait(false);
    }

    public async Task StageToRemoteAsync(
        string hostname,
        NetworkCredential credential,
        CancellationToken ct)
    {
        _logger.Information("Staging HPIA to remote machine {Hostname}", hostname);

        // Acquire per-host lock to prevent TOCTOU race when multiple tasks stage concurrently
        var hostLock = _hostLocks.GetOrAdd(hostname, _ => new SemaphoreSlim(1, 1));
        await hostLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        var localHpiaPath = AppSettings.HpiaExtractPath;

        if (!Directory.Exists(localHpiaPath))
        {
            throw new InvalidOperationException(
                $"HPIA not extracted locally at {localHpiaPath}. Call ExtractLocallyAsync first.");
        }

        // Check if HPIA binaries already staged (in-memory cache + UNC check)
        if (_stagedHosts.ContainsKey(hostname))
        {
            _logger.Information("HPIA already staged on {Hostname} this session, skipping binary copy", hostname);
        }
        else
        {
            bool alreadyStaged = false;
            try
            {
                var uncExe = $"\\\\{hostname}\\{RemoteHpiaExe[0]}${RemoteHpiaExe.Substring(2)}";
                var uncDir = $"\\\\{hostname}\\{RemoteHpiaPath[0]}${RemoteHpiaPath.Substring(2)}";
                // Verify exe exists AND enough files are present (full staging has 70+ files)
                if (File.Exists(uncExe) && Directory.Exists(uncDir))
                {
                    var fileCount = Directory.GetFiles(uncDir, "*.dll").Length;
                    alreadyStaged = fileCount >= 30; // Full HPIA has 50+ DLLs
                    if (!alreadyStaged)
                        _logger.Warning("HPIA on {Hostname} appears incomplete ({FileCount} DLLs found, expected 30+). Re-staging.", hostname, fileCount);
                }
            }
            catch (Exception ex) { Log.Debug("UNC pre-check failed for {Host}: {Error}", hostname, ex.Message); }

            if (alreadyStaged)
            {
                _logger.Information("HPIA already staged on {Hostname}, skipping binary copy", hostname);
            }
            else
            {
                // Clean up incomplete staging before re-copying
                try
                {
                    var uncDir = $"\\\\{hostname}\\{RemoteHpiaPath[0]}${RemoteHpiaPath.Substring(2)}";
                    if (Directory.Exists(uncDir))
                    {
                        _logger.Information("Cleaning incomplete HPIA staging on {Hostname} before re-staging", hostname);
                        Directory.Delete(uncDir, true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Could not clean old staging on {Hostname}, proceeding with overwrite", hostname);
                }

                // Stage HPIA binaries
                await _fileTransfer.CopyToRemoteAsync(
                    hostname,
                    credential,
                    localHpiaPath,
                    RemoteHpiaPath,
                    ct).ConfigureAwait(false);
            }

            _stagedHosts.TryAdd(hostname, 0);
        }

        if (AppSettings.UseOfflineRepository)
        {
            await StageRepositoryToRemoteAsync(hostname, credential, ct).ConfigureAwait(false);
        }
        else
        {
            _logger.Information("Using HPIA online mode on {Hostname}", hostname);
        }

        _logger.Information("HPIA staged successfully to {Hostname}:{RemotePath}", hostname, RemoteHpiaPath);
        }
        finally
        {
            hostLock.Release();
        }
    }

    public async Task<List<HpiaRecommendation>> RunAnalysisAsync(
        Device device,
        NetworkCredential credential,
        CancellationToken ct,
        IProgress<string>? progress = null)
    {
        _logger.Information("Running HPIA analysis on {Hostname}", device.Hostname);
        progress?.Report($"Starting HPIA analysis on {device.Hostname}...");

        var offlineModeArg = GetOfflineModeArgument();
        var remoteLogPath = Path.Combine(RemoteHpiaPath, "HpiaLogs");
        var commandLine =
            $"\"{RemoteHpiaExe}\" /Operation:Analyze /Category:All /Selection:All " +
            $"/Action:List /Silent /Noninteractive /ReportFolder:\"{RemoteReportsPath}\" " +
            $"/Debug /LogFolder:\"{remoteLogPath}\"" +
            offlineModeArg;

        _logger.Information("Using {Mode} mode for analysis on {Hostname}",
            AppSettings.UseOfflineRepository ? "offline repository" : "online", device.Hostname);

        var timeout = TimeSpan.FromMinutes(AppSettings.AnalysisTimeoutMinutes);
        var analysisStart = DateTime.UtcNow;

        var result = await _remoteExecutor.ExecuteAsync(
            device.Hostname,
            credential,
            commandLine,
            null,
            timeout,
            ct,
            progress).ConfigureAwait(false);

        var analysisElapsed = DateTime.UtcNow - analysisStart;
        _logger.Information("HPIA analysis completed on {Hostname} with exit code {ExitCode}: {ExitMessage} (took {Elapsed:F1}s)",
            device.Hostname, result.ExitCode, HpiaExitCodes.GetMessage(result.ExitCode), analysisElapsed.TotalSeconds);

        // Non-success exit codes
        if (!HpiaExitCodes.IsSuccess(result.ExitCode))
        {
            var msg = HpiaExitCodes.GetMessage(result.ExitCode);
            _logger.Error("HPIA analysis failed on {Hostname}: {Message}", device.Hostname, msg);
            throw new InvalidOperationException($"HPIA analysis failed on {device.Hostname}: {msg} (exit code {result.ExitCode})");
        }

        if (HpiaExitCodes.RequiresReboot(result.ExitCode))
        {
            _logger.Warning("Device {Hostname} requires a reboot after analysis (exit code {ExitCode})",
                device.Hostname, result.ExitCode);
        }

        // Copy reports back locally for parsing
        var localReportPath = Path.Combine(
            Path.GetTempPath(), "HPPAQDeploy", "Reports", device.Hostname);

        if (Directory.Exists(localReportPath))
            Directory.Delete(localReportPath, true);

        Directory.CreateDirectory(localReportPath);

        // HPIA may not create a Reports folder if the device is fully up to date
        var remoteReportsUnc = $"\\\\{device.Hostname}\\{RemoteReportsPath[0]}${RemoteReportsPath.Substring(2)}";
        var recommendations = new List<HpiaRecommendation>();

        try
        {
            if (Directory.Exists(remoteReportsUnc))
            {
                await _fileTransfer.CopyFromRemoteAsync(
                    device.Hostname,
                    credential,
                    RemoteReportsPath,
                    localReportPath,
                    ct).ConfigureAwait(false);

                // Parse the reports
                recommendations = _reportParser.ParseReportDirectory(localReportPath, device.Id);
            }
            else if (analysisElapsed.TotalSeconds < 15)
            {
                // HPIA completed too quickly with no reports — likely a launcher error (e.g., 0xB dialog)
                _logger.Warning("HPIA completed in {Elapsed:F1}s with no reports on {Hostname} — possible launcher error. " +
                    "Try running HPIA manually on this device or update HPIA to the latest version.",
                    analysisElapsed.TotalSeconds, device.Hostname);
                progress?.Report($"{device.Hostname}: HPIA completed but produced no report — possible launcher error (try updating HPIA)");
            }
            else
            {
                _logger.Information("No reports directory found on {Hostname} — device is fully up to date", device.Hostname);
            }
        }
        catch (FileNotFoundException)
        {
            _logger.Information("No reports found on {Hostname} — device may be fully up to date", device.Hostname);
        }

        _logger.Information("Found {Count} recommendations for {Hostname}",
            recommendations.Count, device.Hostname);

        return recommendations;
    }

    private async Task StageRepositoryToRemoteAsync(
        string hostname,
        NetworkCredential credential,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(AppSettings.RepositorySharePath))
        {
            _logger.Information("Using shared offline repository for {Hostname}: {RepositorySharePath}",
                hostname, AppSettings.RepositorySharePath);
            return;
        }

        var localRepoPath = AppSettings.RepositoryPath;
        if (!Directory.Exists(localRepoPath) ||
            Directory.GetFiles(localRepoPath, "*", SearchOption.AllDirectories).Length == 0)
        {
            throw new InvalidOperationException(
                $"Offline repository mode is enabled, but no repository files were found at {localRepoPath}. " +
                "Sync the catalog first or configure a Repository Share Path.");
        }

        if (_repoStagedHosts.ContainsKey(hostname))
        {
            _logger.Information("Offline repository already staged on {Hostname} this session, skipping copy", hostname);
            return;
        }

        bool alreadyStaged = false;
        try
        {
            var uncRepoDir = ToUncPath(hostname, RemoteRepoPath);
            alreadyStaged = Directory.Exists(uncRepoDir) &&
                Directory.GetFiles(uncRepoDir, "*", SearchOption.AllDirectories).Length > 0;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Offline repository pre-check failed for {Hostname}", hostname);
        }

        if (!alreadyStaged)
        {
            _logger.Information("Staging offline repository to {Hostname}:{RemoteRepoPath}", hostname, RemoteRepoPath);
            await _fileTransfer.CopyToRemoteAsync(
                hostname,
                credential,
                localRepoPath,
                RemoteRepoPath,
                ct).ConfigureAwait(false);
        }

        _repoStagedHosts.TryAdd(hostname, 0);
    }

    public async Task DeployUpdatesAsync(
        Device device,
        NetworkCredential credential,
        IReadOnlyList<HpiaRecommendation> selectedRecommendations,
        IProgress<string> progress,
        CancellationToken ct)
    {
        _logger.Information("Deploying HPIA updates to {Hostname} ({Count} selected)",
            device.Hostname, selectedRecommendations.Count);

        // Ensure HPIA is staged on the remote machine before deploying
        progress?.Report($"Ensuring HPIA is staged on {device.Hostname}...");
        await ExtractLocallyAsync(ct).ConfigureAwait(false);
        await StageToRemoteAsync(device.Hostname, credential, ct).ConfigureAwait(false);

        progress?.Report($"Starting HPIA deployment on {device.Hostname}...");

        // Clean up previous runs' Download/Report folders to ensure clean progress tracking
        try
        {
            progress?.Report($"Preparing remote directories on {device.Hostname}...");
            await _fileTransfer.DeleteRemoteDirectoryAsync(device.Hostname, credential, RemoteDownloadsPath, ct).ConfigureAwait(false);
            await _fileTransfer.DeleteRemoteDirectoryAsync(device.Hostname, credential, RemoteReportsPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to clean up pre-deployment directories on {Hostname}", device.Hostname);
        }

        var remoteLogPath = Path.Combine(RemoteHpiaPath, "HpiaLogs");
        var biosFlags = "";
        if (AppSettings.BiosPasswords.Count > 0)
        {
            biosFlags = $" /BIOSPwdEnv:{RemoteBiosPasswordEnvVar}";
            _logger.Information("Configured BIOS password environment variable for {Hostname}", device.Hostname);
        }

        // Build SoftPaq list from selected recommendations
        var softPaqIds = selectedRecommendations
            .Select(r => r.SoftPaqId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList();

        // Build the HPIA command line
        string commandLine;
        var offlineModeArg = GetOfflineModeArgument();

        if (softPaqIds.Count > 0)
        {
            // Write a SoftPaq list file to the remote machine.
            // HPIA's /SPList expects a text file with one numeric ID per line (no "sp" prefix).
            var spListFile = Path.Combine(RemoteHpiaPath, "splist.txt");
            var numericIds = softPaqIds
                .Select(id => id.StartsWith("sp", StringComparison.OrdinalIgnoreCase) ? id.Substring(2) : id)
                .ToList();
            try
            {
                var uncSpListPath = $"\\\\{device.Hostname}\\{spListFile[0]}${spListFile.Substring(2)}";
                File.WriteAllLines(uncSpListPath, numericIds);
                _logger.Information("Wrote SPList file to {Hostname} with {Count} SoftPaq(s): {Ids}",
                    device.Hostname, numericIds.Count, string.Join(", ", numericIds));
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to write SPList file to {Hostname}, falling back to full install", device.Hostname);
            }

            // Use /SPList to tell HPIA to only install the specified SoftPaqs
            commandLine =
                $"\"{RemoteHpiaExe}\" /Operation:Analyze /Action:Install " +
                $"/Silent /Noninteractive " +
                $"/SoftpaqDownloadFolder:\"{RemoteDownloadsPath}\" " +
                $"/ReportFolder:\"{RemoteReportsPath}\"" +
                $" /Debug /LogFolder:\"{remoteLogPath}\"" +
                $" /SPList:\"{spListFile}\"" +
                offlineModeArg +
                biosFlags;
        }
        else
        {
            // No specific SoftPaqs selected — install all recommended updates
            commandLine =
                $"\"{RemoteHpiaExe}\" /Operation:Analyze /Category:All /Selection:All " +
                $"/Action:Install /Silent /Noninteractive " +
                $"/SoftpaqDownloadFolder:\"{RemoteDownloadsPath}\" " +
                $"/ReportFolder:\"{RemoteReportsPath}\"" +
                $" /Debug /LogFolder:\"{remoteLogPath}\"" +
                offlineModeArg +
                biosFlags;
        }

        _logger.Information("Deploying {Count} SoftPaq(s) to {Hostname}: {SoftPaqs}",
            softPaqIds.Count, device.Hostname, string.Join(", ", softPaqIds));

        var timeout = TimeSpan.FromMinutes(AppSettings.DeployTimeoutMinutes);

        progress?.Report($"Executing HPIA Install on {device.Hostname} (this may take several minutes)...");

        var result = await _remoteExecutor.ExecuteAsync(
            device.Hostname,
            credential,
            commandLine,
            null,
            timeout,
            ct,
            progress).ConfigureAwait(false);

        var exitMessage = HpiaExitCodes.GetMessage(result.ExitCode);

        if (HpiaExitCodes.IsSuccess(result.ExitCode))
        {
            _logger.Information("HPIA deployment completed on {Hostname} (ExitCode={ExitCode}): {ExitMessage}",
                device.Hostname, result.ExitCode, exitMessage);

            if (HpiaExitCodes.RequiresReboot(result.ExitCode))
            {
                device.Status = DeviceStatus.RebootRequired;
                device.NeedsReboot = true;
                progress?.Report($"Deployment completed on {device.Hostname}. {exitMessage}.");
            }
            else
            {
                progress?.Report($"Deployment completed successfully on {device.Hostname}.");
            }

            return;
        }

        _logger.Error("HPIA deployment failed on {Hostname} (ExitCode={ExitCode}): {ExitMessage}",
            device.Hostname, result.ExitCode, exitMessage);
        progress?.Report($"Deployment failed on {device.Hostname}: {exitMessage}");
        throw new InvalidOperationException(
            $"HPIA deployment failed on {device.Hostname}: {exitMessage} (exit code {result.ExitCode})");
    }

    public async Task CleanupRemoteAsync(
        string hostname,
        NetworkCredential credential,
        CancellationToken ct)
    {
        _logger.Information("Cleaning up HPIA files on {Hostname}", hostname);

        await _fileTransfer.DeleteRemoteDirectoryAsync(
            hostname,
            credential,
            RemoteHpiaPath,
            ct).ConfigureAwait(false);

        // Remove from staging caches so next operation re-stages
        _stagedHosts.TryRemove(hostname, out _);
        _repoStagedHosts.TryRemove(hostname, out _);

        _logger.Information("Cleanup completed on {Hostname}", hostname);
    }

    private static string GetOfflineModeArgument()
    {
        if (!AppSettings.UseOfflineRepository)
            return "";

        var repositoryPath = !string.IsNullOrWhiteSpace(AppSettings.RepositorySharePath)
            ? AppSettings.RepositorySharePath.Trim()
            : RemoteRepoPath;

        return $" /Offlinemode:\"{repositoryPath}\"";
    }

    private static string ToUncPath(string hostname, string remotePath)
    {
        if (remotePath.Length >= 2 && remotePath[1] == ':')
            return $"\\\\{hostname}\\{remotePath[0]}${remotePath.Substring(2)}";

        return remotePath.StartsWith(@"\\", StringComparison.Ordinal)
            ? remotePath
            : $"\\\\{hostname}\\{remotePath}";
    }
}

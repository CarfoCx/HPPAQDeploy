using System.Diagnostics;
using HPPAQDeploy.Shared.Configuration;
using Serilog;

namespace HPPAQDeploy.Infrastructure.Hpia;

/// <summary>
/// Syncs an offline HPIA repository using HP Client Management Script Library (HPCMSL).
/// Downloads reference files and SoftPaq metadata for specified platform IDs.
/// </summary>
public class RepositorySyncer
{
    private readonly ILogger _logger = Log.ForContext<RepositorySyncer>();

    /// <summary>
    /// Ensures HPCMSL PowerShell module is installed.
    /// </summary>
    public async Task EnsureHpcmslInstalledAsync(CancellationToken ct)
    {
        _logger.Information("Checking if HPCMSL module is installed...");

        var checkScript = "if (Get-Module -ListAvailable -Name HPCMSL) { Write-Output 'INSTALLED' } else { Write-Output 'NOTINSTALLED' }";
        var result = await RunPowerShellAsync(checkScript, ct);

        if (result.Contains("NOTINSTALLED"))
        {
            _logger.Information("Installing HPCMSL module (updating PowerShellGet first)...");

            // Step 1: Update PowerShellGet so it supports PowerShellGetFormatVersion 2.0 and -AcceptLicense
            var updatePsGetScript = "Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force; " +
                "Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope CurrentUser | Out-Null; " +
                "Install-Module -Name PowerShellGet -Force -Scope CurrentUser -AllowClobber";
            await RunPowerShellAsync(updatePsGetScript, ct, timeoutMinutes: 5);
            _logger.Information("PowerShellGet updated");

            // Step 2: Install HPCMSL with the updated PowerShellGet (must re-import it first)
            var installScript = "Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force; " +
                "Import-Module PowerShellGet -Force; " +
                "Install-Module -Name HPCMSL -Force -AcceptLicense -Scope CurrentUser";
            await RunPowerShellAsync(installScript, ct, timeoutMinutes: 5);

            // Verify installation actually succeeded
            var verifyResult = await RunPowerShellAsync(checkScript, ct);
            if (verifyResult.Contains("NOTINSTALLED"))
            {
                _logger.Error("HPCMSL module failed to install. Repository sync will not work.");
                throw new InvalidOperationException(
                    "HPCMSL PowerShell module failed to install. " +
                    "Try running these commands manually in an elevated PowerShell window:\n" +
                    "  Install-Module -Name PowerShellGet -Force -AllowClobber\n" +
                    "  Import-Module PowerShellGet -Force\n" +
                    "  Install-Module -Name HPCMSL -Force -AcceptLicense");
            }

            _logger.Information("HPCMSL module installed successfully");
        }
        else
        {
            _logger.Information("HPCMSL module is already installed");
        }
    }

    /// <summary>
    /// Syncs the offline repository for the given platform IDs and OS.
    /// Platform IDs are the 4-character HP platform codes (e.g., "8870" for Z2 SFF G9).
    /// </summary>
    public async Task SyncRepositoryAsync(
        IEnumerable<string> platformIds,
        string os = "Win10",
        string osVer = "22H2",
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var repoPath = AppSettings.RepositoryPath;
        Directory.CreateDirectory(repoPath);

        _logger.Information("Syncing offline repository to {RepoPath}", repoPath);
        progress?.Report("Ensuring HPCMSL module is installed...");

        await EnsureHpcmslInstalledAsync(ct);

        // Build the PowerShell script for repository sync
        var platforms = platformIds.Distinct().ToList();
        if (platforms.Count == 0)
        {
            _logger.Warning("No platform IDs provided for repository sync");
            progress?.Report("No platform IDs found. Scan devices first to detect HP platforms.");
            return;
        }

        _logger.Information("Syncing repository for {Count} platforms: {Platforms}",
            platforms.Count, string.Join(", ", platforms));
        progress?.Report($"Initializing repository for {platforms.Count} platform(s)...");

        // Build the sync script
        var script = BuildSyncScript(repoPath, platforms, os, osVer);

        _logger.Debug("Running repository sync script");
        progress?.Report("Downloading reference files and SoftPaq metadata from HP.com...");

        var output = await RunPowerShellAsync(script, ct, timeoutMinutes: 30);
        _logger.Information("Repository sync output: {Output}", output);

        // Verify repo has content
        var fileCount = Directory.Exists(repoPath)
            ? Directory.GetFiles(repoPath, "*", SearchOption.AllDirectories).Length
            : 0;

        if (fileCount > 0)
        {
            progress?.Report($"Repository synced successfully. {fileCount} files downloaded.");
            _logger.Information("Repository sync completed. {FileCount} files in {RepoPath}", fileCount, repoPath);
        }
        else
        {
            progress?.Report("Repository sync completed but no files were downloaded. Check logs.");
            _logger.Warning("Repository sync completed but no files found in {RepoPath}", repoPath);
        }
    }

    private string BuildSyncScript(string repoPath, List<string> platforms, string os, string osVer)
    {
        var escapedPath = repoPath.Replace("'", "''");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force");
        sb.AppendLine("Import-Module PowerShellGet -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Import-Module HPCMSL -Force");
        sb.AppendLine($"$repoPath = '{escapedPath}'");
        sb.AppendLine("Set-Location $repoPath");
        sb.AppendLine();

        // Always re-initialize to ensure clean state
        sb.AppendLine("Write-Output 'Initializing repository...'");
        sb.AppendLine("if (Test-Path (Join-Path $repoPath '.repository')) {");
        sb.AppendLine("    # Remove existing filters to start fresh");
        sb.AppendLine("    try { Remove-RepositoryFilter -Platform * -Yes -ErrorAction SilentlyContinue } catch {}");
        sb.AppendLine("} else {");
        sb.AppendLine("    Initialize-Repository");
        sb.AppendLine("}");
        sb.AppendLine();

        // Set offline cache mode
        sb.AppendLine("Write-Output 'Enabling offline cache mode...'");
        sb.AppendLine("Set-RepositoryConfiguration -Setting OfflineCacheMode -CacheValue Enable");
        sb.AppendLine();

        // Add filters for each platform with multiple OS versions for maximum coverage
        // Not all platforms support all OS versions, so we add multiple and let HPCMSL skip what doesn't exist
        var osVersions = new List<(string os, string ver)>
        {
            ("Win11", "24H2"), ("Win11", "23H2"), ("Win10", "22H2")
        };

        foreach (var platformId in platforms)
        {
            foreach (var (osName, osVersion) in osVersions)
            {
                sb.AppendLine($"try {{ Add-RepositoryFilter -Platform '{platformId}' -Os '{osName}' -OsVer '{osVersion}' -Category Bios,Firmware,Driver,Software -ErrorAction SilentlyContinue }} catch {{ }}");
            }
            sb.AppendLine($"Write-Output 'Added filters for platform {platformId}'");
        }
        sb.AppendLine();

        // Sync the repository - downloads SoftPaq metadata
        sb.AppendLine("Write-Output 'Starting repository sync (this may take several minutes)...'");
        sb.AppendLine("Invoke-RepositorySync -Verbose");
        sb.AppendLine();

        // Show what was downloaded
        sb.AppendLine("$files = Get-ChildItem -Path $repoPath -Recurse -File");
        sb.AppendLine("Write-Output \"Repository sync complete. $($files.Count) files downloaded.\"");
        sb.AppendLine("$exes = $files | Where-Object { $_.Extension -eq '.exe' }");
        sb.AppendLine("if ($exes.Count -gt 0) { Write-Output \"SoftPaq executables: $($exes.Count)\" }");

        return sb.ToString();
    }

    private async Task<string> RunPowerShellAsync(string script, CancellationToken ct, int timeoutMinutes = 10)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Create a linked token that respects both user cancellation and the timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
        var linkedCt = timeoutCts.Token;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start PowerShell");

        var output = await process.StandardOutput.ReadToEndAsync(linkedCt);
        var error = await process.StandardError.ReadToEndAsync(linkedCt);

        await process.WaitForExitAsync(linkedCt);

        if (!string.IsNullOrWhiteSpace(error))
            _logger.Warning("PowerShell stderr: {Error}", error);

        return output.Trim();
    }

    /// <summary>
    /// Runs the sync script from a temp file to avoid command-line escaping issues.
    /// Streams output line-by-line so the user sees real-time progress.
    /// Returns the number of files in the repository after sync.
    /// </summary>
    public async Task<int> SyncRepositoryViaScriptFileAsync(
        IEnumerable<string> platformIds,
        string os = "Win10",
        string osVer = "22H2",
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var repoPath = AppSettings.RepositoryPath;
        Directory.CreateDirectory(repoPath);

        progress?.Report("Ensuring HPCMSL module is installed...");
        await EnsureHpcmslInstalledAsync(ct);

        var platforms = platformIds.Distinct().ToList();
        if (platforms.Count == 0)
        {
            progress?.Report("No platform IDs found. Scan devices first.");
            return 0;
        }

        progress?.Report($"Syncing repository for {platforms.Count} platform(s): {string.Join(", ", platforms)}");

        var script = BuildSyncScript(repoPath, platforms, os, osVer);
        var scriptPath = Path.Combine(Path.GetTempPath(), $"hpia_repo_sync_{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, script, ct);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start PowerShell");

            // Stream stdout line-by-line for real-time progress
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _logger.Information("HPCMSL: {Line}", line);
                    progress?.Report($"HPCMSL: {line}");
                }
            }

            var error = await errorTask;
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                _logger.Warning("Repository sync exited with code {ExitCode}", process.ExitCode);
                if (!string.IsNullOrWhiteSpace(error))
                {
                    _logger.Warning("Repository sync stderr: {Error}", error);
                    // Show first meaningful error line to user
                    var firstError = error.Split('\n')
                        .Select(l => l.Trim())
                        .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("WARNING:"));
                    if (!string.IsNullOrEmpty(firstError))
                        progress?.Report($"Sync warning: {firstError}");
                }
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                _logger.Debug("Repository sync stderr (non-fatal): {Error}", error);
            }

            // Small delay to let file system settle
            await Task.Delay(500, ct);

            var fileCount = Directory.Exists(repoPath)
                ? Directory.GetFiles(repoPath, "*", SearchOption.AllDirectories).Length
                : 0;

            if (fileCount > 0)
                progress?.Report($"Repository synced successfully! {fileCount} files downloaded.");
            else
                progress?.Report("Sync completed but no files were downloaded. Check that HPCMSL is installed and platform IDs are valid.");

            _logger.Information("Repository sync completed. {FileCount} files. Exit code: {ExitCode}",
                fileCount, process.ExitCode);

            return fileCount;
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }
}

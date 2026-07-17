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
        var platforms = NormalizePlatformIds(platformIds);
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

        // Honor the fleet OS detected by the caller, then add currently supported
        // fallback catalogs for mixed fleets. Distinct prevents duplicate filters.
        var osVersions = new List<(string os, string ver)>
        {
            (os, osVer), ("Win11", "24H2"), ("Win11", "23H2"), ("Win10", "22H2")
        }
        .Where(item => !string.IsNullOrWhiteSpace(item.os) && !string.IsNullOrWhiteSpace(item.ver))
        .Distinct()
        .ToList();

        foreach (var platformId in platforms)
        {
            var escapedPlatformId = platformId.Replace("'", "''");
            foreach (var (osName, osVersion) in osVersions)
            {
                var escapedOsName = osName.Replace("'", "''");
                var escapedOsVersion = osVersion.Replace("'", "''");
                sb.AppendLine($"try {{ Add-RepositoryFilter -Platform '{escapedPlatformId}' -Os '{escapedOsName}' -OsVer '{escapedOsVersion}' -Category Bios,Firmware,Driver,Software -ErrorAction SilentlyContinue }} catch {{ }}");
            }
            sb.AppendLine($"Write-Output 'Added filters for platform {escapedPlatformId}'");
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

    private static List<string> NormalizePlatformIds(IEnumerable<string> platformIds) =>
        platformIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async Task<string> RunPowerShellAsync(string script, CancellationToken ct, int timeoutMinutes = 10)
    {
        if (timeoutMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMinutes));

        var scriptPath = Path.Combine(Path.GetTempPath(), $"hppaq-powershell-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, script, ct).ConfigureAwait(false);
        try
        {
            var psi = CreatePowerShellStartInfo(scriptPath);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start PowerShell");
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await StopProcessAsync(process).ConfigureAwait(false);
                throw new TimeoutException($"PowerShell operation timed out after {timeoutMinutes} minutes.");
            }
            catch (OperationCanceledException)
            {
                await StopProcessAsync(process).ConfigureAwait(false);
                throw;
            }

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"PowerShell exited with code {process.ExitCode}: {FirstMeaningfulLine(error)}");

            if (!string.IsNullOrWhiteSpace(error))
                _logger.Warning("PowerShell stderr: {Error}", error);

            return output.Trim();
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
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

        var platforms = NormalizePlatformIds(platformIds);
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
            var psi = CreatePowerShellStartInfo(scriptPath);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start PowerShell");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(30));
            var linkedCt = timeoutCts.Token;

            // Stream stdout line-by-line for real-time progress
            var errorTask = process.StandardError.ReadToEndAsync(linkedCt);
            try
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync(linkedCt).ConfigureAwait(false)) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _logger.Information("HPCMSL: {Line}", line);
                        progress?.Report($"HPCMSL: {line}");
                    }
                }

                await process.WaitForExitAsync(linkedCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await StopProcessAsync(process).ConfigureAwait(false);
                throw new TimeoutException("Repository sync timed out after 30 minutes.");
            }
            catch (OperationCanceledException)
            {
                await StopProcessAsync(process).ConfigureAwait(false);
                throw;
            }

            var error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var firstError = FirstMeaningfulLine(error);
                _logger.Error("Repository sync exited with code {ExitCode}: {Error}", process.ExitCode, firstError);
                throw new InvalidOperationException(
                    $"Repository sync failed with exit code {process.ExitCode}: {firstError}");
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

    private static ProcessStartInfo CreatePowerShellStartInfo(string scriptPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        return psi;
    }

    private static string FirstMeaningfulLine(string error)
    {
        return error.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
            ?? "No error details were provided.";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best effort during cancellation/timeout.
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        TryKill(process);
        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Do not turn cancellation into an unbounded wait.
        }
    }
}

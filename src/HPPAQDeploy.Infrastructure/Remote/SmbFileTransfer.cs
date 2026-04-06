using System.Diagnostics;
using System.Net;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Shared.Configuration;
using HPPAQDeploy.Shared.Helpers;
using Serilog;

namespace HPPAQDeploy.Infrastructure.Remote;

/// <summary>
/// Implements IFileTransfer using SMB (net use) and System.IO file operations.
/// </summary>
public class SmbFileTransfer : IFileTransfer
{
    private readonly ILogger _logger = Log.ForContext<SmbFileTransfer>();

    public async Task CopyToRemoteAsync(
        string hostname,
        NetworkCredential credential,
        string localPath,
        string remotePath,
        CancellationToken ct)
    {
        await RetryHelper.RetryAsync(async () =>
        {
            await CopyToRemoteInternalAsync(hostname, credential, localPath, remotePath, ct)
                .ConfigureAwait(false);
        }, ct: ct).ConfigureAwait(false);
    }

    private async Task CopyToRemoteInternalAsync(
        string hostname,
        NetworkCredential credential,
        string localPath,
        string remotePath,
        CancellationToken ct)
    {
        var uncShare = $"\\\\{hostname}\\C$";
        var uncTargetPath = ConvertToUncPath(hostname, remotePath);

        // Apply file transfer timeout to prevent indefinite hangs on slow/unreachable shares
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(AppSettings.FileTransferTimeoutMinutes));
        var linkedCt = timeoutCts.Token;

        try
        {
            await ConnectSmbShareAsync(uncShare, credential, linkedCt).ConfigureAwait(false);

            if (Directory.Exists(localPath))
            {
                _logger.Information("Copying directory {LocalPath} to {UncPath}", localPath, uncTargetPath);
                await CopyDirectoryRecursiveAsync(localPath, uncTargetPath, linkedCt).ConfigureAwait(false);
            }
            else if (File.Exists(localPath))
            {
                _logger.Information("Copying file {LocalPath} to {UncPath}", localPath, uncTargetPath);
                var targetDir = Path.GetDirectoryName(uncTargetPath)!;
                Directory.CreateDirectory(targetDir);
                await using var sourceStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await using var destStream = new FileStream(uncTargetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await sourceStream.CopyToAsync(destStream, 81920, linkedCt).ConfigureAwait(false);
            }
            else
            {
                throw new FileNotFoundException($"Source path not found: {localPath}");
            }

            _logger.Information("Successfully copied to {Hostname}:{RemotePath}", hostname, remotePath);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"File transfer to {hostname} timed out after {AppSettings.FileTransferTimeoutMinutes} minutes");
        }
        finally
        {
            await DisconnectSmbShareAsync(uncShare, ct).ConfigureAwait(false);
        }
    }

    public async Task CopyFromRemoteAsync(
        string hostname,
        NetworkCredential credential,
        string remotePath,
        string localPath,
        CancellationToken ct)
    {
        var uncShare = $"\\\\{hostname}\\C$";
        var uncSourcePath = ConvertToUncPath(hostname, remotePath);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(AppSettings.FileTransferTimeoutMinutes));
        var linkedCt = timeoutCts.Token;

        try
        {
            await ConnectSmbShareAsync(uncShare, credential, linkedCt).ConfigureAwait(false);

            if (Directory.Exists(uncSourcePath))
            {
                _logger.Information("Copying remote directory {UncPath} to {LocalPath}", uncSourcePath, localPath);
                await CopyDirectoryRecursiveAsync(uncSourcePath, localPath, linkedCt).ConfigureAwait(false);
            }
            else if (File.Exists(uncSourcePath))
            {
                _logger.Information("Copying remote file {UncPath} to {LocalPath}", uncSourcePath, localPath);
                var targetDir = Path.GetDirectoryName(localPath)!;
                Directory.CreateDirectory(targetDir);
                await using var sourceStream = new FileStream(uncSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await using var destStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await sourceStream.CopyToAsync(destStream, 81920, linkedCt).ConfigureAwait(false);
            }
            else
            {
                throw new FileNotFoundException($"Remote path not found: {uncSourcePath}");
            }

            _logger.Information("Successfully copied from {Hostname}:{RemotePath}", hostname, remotePath);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"File transfer from {hostname} timed out after {AppSettings.FileTransferTimeoutMinutes} minutes");
        }
        finally
        {
            await DisconnectSmbShareAsync(uncShare, ct).ConfigureAwait(false);
        }
    }

    public async Task DeleteRemoteDirectoryAsync(
        string hostname,
        NetworkCredential credential,
        string remotePath,
        CancellationToken ct)
    {
        var uncShare = $"\\\\{hostname}\\C$";
        var uncTargetPath = ConvertToUncPath(hostname, remotePath);

        try
        {
            await ConnectSmbShareAsync(uncShare, credential, ct).ConfigureAwait(false);

            if (Directory.Exists(uncTargetPath))
            {
                _logger.Information("Deleting remote directory {UncPath}", uncTargetPath);
                Directory.Delete(uncTargetPath, recursive: true);
                _logger.Information("Successfully deleted {UncPath}", uncTargetPath);
            }
            else
            {
                _logger.Information("Remote directory {UncPath} does not exist, nothing to delete", uncTargetPath);
            }
        }
        finally
        {
            await DisconnectSmbShareAsync(uncShare, ct).ConfigureAwait(false);
        }
    }

    public async Task<bool> TestConnectionAsync(
        string hostname,
        NetworkCredential credential,
        CancellationToken ct)
    {
        var uncShare = $"\\\\{hostname}\\C$";

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            await ConnectSmbShareAsync(uncShare, credential, timeoutCts.Token).ConfigureAwait(false);

            var exists = Directory.Exists(uncShare);

            await DisconnectSmbShareAsync(uncShare, ct).ConfigureAwait(false);

            return exists;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.Warning("SMB connection test to {Hostname} timed out", hostname);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "SMB connection test to {Hostname} failed", hostname);
            return false;
        }
    }

    private async Task ConnectSmbShareAsync(string uncShare, NetworkCredential credential, CancellationToken ct)
    {
        string username = string.IsNullOrEmpty(credential.Domain)
            ? credential.UserName
            : $"{credential.Domain}\\{credential.UserName}";

        var args = $"use \"{uncShare}\" /user:\"{username}\" \"{credential.Password}\"";

        _logger.Debug("Connecting to SMB share {UncShare}", uncShare);
        await RunNetCommandAsync(args, ct).ConfigureAwait(false);
    }

    private async Task DisconnectSmbShareAsync(string uncShare, CancellationToken ct)
    {
        try
        {
            var args = $"use {uncShare} /delete /y";
            _logger.Debug("Disconnecting from SMB share {UncShare}", uncShare);
            await RunNetCommandAsync(args, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Disconnection failures are non-critical
            _logger.Warning(ex, "Failed to disconnect SMB share {UncShare}", uncShare);
        }
    }

    private static async Task RunNetCommandAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "net",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start net command process");

        // Read stdout/stderr BEFORE WaitForExitAsync to avoid deadlock.
        // If the process fills the OS pipe buffer while we're waiting for exit,
        // both sides block forever (process can't write, we can't read).
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var error = await errorTask.ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var combined = $"{error} {output}".Trim();
            // System error 1219 means connection already exists - not a real error
            if (!combined.Contains("1219"))
            {
                throw new InvalidOperationException(
                    $"net {arguments.Split(' ')[0]} failed with exit code {process.ExitCode}: {combined}");
            }
        }
    }

    private static async Task CopyDirectoryRecursiveAsync(string sourceDir, string targetDir, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            var destFile = Path.Combine(targetDir, Path.GetFileName(file));
            // Use async file streams for UNC copies to avoid blocking the thread pool
            await using var sourceStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var destStream = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await sourceStream.CopyToAsync(destStream, 81920, ct).ConfigureAwait(false);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            var destDir = Path.Combine(targetDir, Path.GetFileName(dir));
            await CopyDirectoryRecursiveAsync(dir, destDir, ct).ConfigureAwait(false);
        }
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
}

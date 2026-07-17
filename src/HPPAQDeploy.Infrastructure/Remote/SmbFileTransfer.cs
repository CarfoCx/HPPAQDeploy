using System.Diagnostics;
using System.Net;
using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> HostLocks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IRemoteFileSession> OpenAuthenticatedSessionAsync(
        string hostname,
        NetworkCredential credential,
        CancellationToken ct)
    {
        ValidateHostname(hostname);
        ArgumentNullException.ThrowIfNull(credential);

        var hostLock = HostLocks.GetOrAdd(hostname, _ => new SemaphoreSlim(1, 1));
        await hostLock.WaitAsync(ct).ConfigureAwait(false);
        var uncShare = $"\\\\{hostname}\\C$";

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(AppSettings.FileTransferTimeoutMinutes));
            await DisconnectSmbShareAsync(uncShare, timeoutCts.Token).ConfigureAwait(false);
            await ConnectSmbShareAsync(uncShare, credential, timeoutCts.Token).ConfigureAwait(false);
            timeoutCts.Token.ThrowIfCancellationRequested();
            return new AuthenticatedRemoteFileSession(this, uncShare, hostLock);
        }
        catch
        {
            await DisconnectSmbShareForCleanupAsync(uncShare).ConfigureAwait(false);
            hostLock.Release();
            throw;
        }
    }

    public async Task CopyToRemoteAsync(
        string hostname,
        NetworkCredential credential,
        string localPath,
        string remotePath,
        CancellationToken ct)
    {
        ValidateHostname(hostname);
        ArgumentNullException.ThrowIfNull(credential);
        var hostLock = HostLocks.GetOrAdd(hostname, _ => new SemaphoreSlim(1, 1));
        await hostLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RetryHelper.RetryAsync(async () =>
            {
                await CopyToRemoteInternalAsync(hostname, credential, localPath, remotePath, ct)
                    .ConfigureAwait(false);
            }, ct: ct).ConfigureAwait(false);
        }
        finally
        {
            hostLock.Release();
        }
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
            await DisconnectSmbShareAsync(uncShare, linkedCt).ConfigureAwait(false);
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
            await DisconnectSmbShareForCleanupAsync(uncShare).ConfigureAwait(false);
        }
    }

    public async Task CopyFromRemoteAsync(
        string hostname,
        NetworkCredential credential,
        string remotePath,
        string localPath,
        CancellationToken ct)
    {
        ValidateHostname(hostname);
        ArgumentNullException.ThrowIfNull(credential);
        var hostLock = HostLocks.GetOrAdd(hostname, _ => new SemaphoreSlim(1, 1));
        await hostLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await CopyFromRemoteInternalAsync(hostname, credential, remotePath, localPath, ct).ConfigureAwait(false);
        }
        finally
        {
            hostLock.Release();
        }
    }

    private async Task CopyFromRemoteInternalAsync(
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
            await DisconnectSmbShareAsync(uncShare, linkedCt).ConfigureAwait(false);
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
            await DisconnectSmbShareForCleanupAsync(uncShare).ConfigureAwait(false);
        }
    }

    public async Task DeleteRemoteDirectoryAsync(
        string hostname,
        NetworkCredential credential,
        string remotePath,
        CancellationToken ct)
    {
        ValidateHostname(hostname);
        ArgumentNullException.ThrowIfNull(credential);
        var hostLock = HostLocks.GetOrAdd(hostname, _ => new SemaphoreSlim(1, 1));
        await hostLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DeleteRemoteDirectoryInternalAsync(hostname, credential, remotePath, ct).ConfigureAwait(false);
        }
        finally
        {
            hostLock.Release();
        }
    }

    private async Task DeleteRemoteDirectoryInternalAsync(
        string hostname,
        NetworkCredential credential,
        string remotePath,
        CancellationToken ct)
    {
        var uncShare = $"\\\\{hostname}\\C$";
        var uncTargetPath = ConvertToUncPath(hostname, remotePath);

        try
        {
            await DisconnectSmbShareAsync(uncShare, ct).ConfigureAwait(false);
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
            await DisconnectSmbShareForCleanupAsync(uncShare).ConfigureAwait(false);
        }
    }

    public async Task<bool> TestConnectionAsync(
        string hostname,
        NetworkCredential credential,
        CancellationToken ct)
    {
        ValidateHostname(hostname);
        ArgumentNullException.ThrowIfNull(credential);
        var hostLock = HostLocks.GetOrAdd(hostname, _ => new SemaphoreSlim(1, 1));
        await hostLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await TestConnectionInternalAsync(hostname, credential, ct).ConfigureAwait(false);
        }
        finally
        {
            hostLock.Release();
        }
    }

    private async Task<bool> TestConnectionInternalAsync(
        string hostname,
        NetworkCredential credential,
        CancellationToken ct)
    {
        var uncShare = $"\\\\{hostname}\\C$";

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            await DisconnectSmbShareAsync(uncShare, timeoutCts.Token).ConfigureAwait(false);
            await ConnectSmbShareAsync(uncShare, credential, timeoutCts.Token).ConfigureAwait(false);

            var exists = Directory.Exists(uncShare);

            return exists;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.Warning("SMB connection test to {Hostname} timed out", hostname);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "SMB connection test to {Hostname} failed", hostname);
            return false;
        }
        finally
        {
            await DisconnectSmbShareForCleanupAsync(uncShare).ConfigureAwait(false);
        }
    }

    private async Task ConnectSmbShareAsync(string uncShare, NetworkCredential credential, CancellationToken ct)
    {
        string username = string.IsNullOrEmpty(credential.Domain)
            ? credential.UserName
            : $"{credential.Domain}\\{credential.UserName}";

        _logger.Debug("Connecting to SMB share {UncShare}", uncShare);
        await RunNetCommandAsync(
            ["use", uncShare, $"/user:{username}", credential.Password],
            ct).ConfigureAwait(false);
    }

    private async Task DisconnectSmbShareAsync(string uncShare, CancellationToken ct)
    {
        try
        {
            _logger.Debug("Disconnecting from SMB share {UncShare}", uncShare);
            await RunNetCommandAsync(["use", uncShare, "/delete", "/y"], ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Disconnection failures are non-critical
            _logger.Warning(ex, "Failed to disconnect SMB share {UncShare}", uncShare);
        }
    }

    private static async Task RunNetCommandAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "net",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start net command process");

        // Read stdout/stderr BEFORE WaitForExitAsync to avoid deadlock.
        // If the process fills the OS pipe buffer while we're waiting for exit,
        // both sides block forever (process can't write, we can't read).
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(process).ConfigureAwait(false);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var error = await errorTask.ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var combined = $"{error} {output}".Trim();
            // System error 1219 means connection already exists - not a real error
            if (!combined.Contains("1219", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"net {arguments[0]} failed with exit code {process.ExitCode}: {combined}");
            }
        }
    }

    private static async Task CopyDirectoryRecursiveAsync(string sourceDir, string targetDir, CancellationToken ct)
    {
        // Try robocopy first - dramatically faster for large directories over UNC
        try
        {
            await CopyWithRobocopyAsync(sourceDir, targetDir, ct).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Robocopy failed, falling back to manual copy from {Source} to {Target}", sourceDir, targetDir);
        }

        // Fallback: manual byte-by-byte copy
        await CopyDirectoryManualAsync(sourceDir, targetDir, ct).ConfigureAwait(false);
    }

    private static async Task CopyWithRobocopyAsync(string sourceDir, string targetDir, CancellationToken ct)
    {
        // /MIR = mirror, /MT:8 = 8 threads, /R:1 /W:1 = 1 retry/1s wait
        // /NJH /NJS /NFL /NDL /NP = suppress output noise
        var args = $"\"{sourceDir}\" \"{targetDir}\" /MIR /MT:8 /R:1 /W:1 /NJH /NJS /NFL /NDL /NP /DCOPY:DA";

        var psi = new ProcessStartInfo
        {
            FileName = "robocopy",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start robocopy");

        // Drain BOTH stdout and stderr before waiting for exit. Even with output
        // suppressed, robocopy can still write enough to fill the OS pipe buffer;
        // if nobody reads it, the process blocks on write and we deadlock on WaitForExit.
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(process).ConfigureAwait(false);
            throw;
        }

        // Robocopy exit codes: 0-7 = success, 8+ = failure
        if (process.ExitCode >= 8)
        {
            var error = await errorTask.ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            throw new InvalidOperationException(
                $"robocopy failed with exit code {process.ExitCode}: {error} {output}".Trim());
        }
    }

    private static async Task CopyDirectoryManualAsync(string sourceDir, string targetDir, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            var destFile = Path.Combine(targetDir, Path.GetFileName(file));
            await using var sourceStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var destStream = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await sourceStream.CopyToAsync(destStream, 81920, ct).ConfigureAwait(false);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            var destDir = Path.Combine(targetDir, Path.GetFileName(dir));
            await CopyDirectoryManualAsync(dir, destDir, ct).ConfigureAwait(false);
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

    private static void ValidateHostname(string hostname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        if (hostname.Length > 255 ||
            hostname is "." or ".." ||
            hostname.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("Hostname contains invalid path characters.", nameof(hostname));
        }
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
            // Process exited between the state check and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best effort during cancellation.
        }
    }

    private async Task DisconnectSmbShareForCleanupAsync(string uncShare)
    {
        // Cleanup must still run after the caller cancels. Keep it independently
        // bounded so a broken SMB provider cannot delay cancellation indefinitely.
        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await DisconnectSmbShareAsync(uncShare, cleanupCts.Token).ConfigureAwait(false);
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

    private sealed class AuthenticatedRemoteFileSession : IRemoteFileSession
    {
        private readonly SmbFileTransfer _owner;
        private readonly string _uncShare;
        private readonly SemaphoreSlim _hostLock;
        private int _disposed;

        public AuthenticatedRemoteFileSession(
            SmbFileTransfer owner,
            string uncShare,
            SemaphoreSlim hostLock)
        {
            _owner = owner;
            _uncShare = uncShare;
            _hostLock = hostLock;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                await _owner.DisconnectSmbShareForCleanupAsync(_uncShare).ConfigureAwait(false);
            }
            finally
            {
                _hostLock.Release();
            }
        }
    }
}

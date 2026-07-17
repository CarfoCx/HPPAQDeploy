using System.Net;

namespace HPPAQDeploy.Core.Interfaces;

/// <summary>
/// Holds an authenticated SMB connection open for a short sequence of direct
/// UNC operations. Dispose it promptly so the host connection and lock are released.
/// </summary>
public interface IRemoteFileSession : IAsyncDisposable
{
}

public interface IFileTransfer
{
    Task<IRemoteFileSession> OpenAuthenticatedSessionAsync(
        string hostname,
        NetworkCredential credential,
        CancellationToken ct);

    Task CopyToRemoteAsync(
        string hostname,
        NetworkCredential credential,
        string localPath,
        string remotePath,
        CancellationToken ct);

    Task CopyFromRemoteAsync(
        string hostname,
        NetworkCredential credential,
        string remotePath,
        string localPath,
        CancellationToken ct);

    Task DeleteRemoteDirectoryAsync(
        string hostname,
        NetworkCredential credential,
        string remotePath,
        CancellationToken ct);

    /// <summary>
    /// Tests SMB connectivity by attempting to access \\hostname\C$.
    /// </summary>
    Task<bool> TestConnectionAsync(
        string hostname,
        NetworkCredential credential,
        CancellationToken ct);
}

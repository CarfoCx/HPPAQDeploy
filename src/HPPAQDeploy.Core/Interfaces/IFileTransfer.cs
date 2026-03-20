using System.Net;

namespace HPPAQDeploy.Core.Interfaces;

public interface IFileTransfer
{
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

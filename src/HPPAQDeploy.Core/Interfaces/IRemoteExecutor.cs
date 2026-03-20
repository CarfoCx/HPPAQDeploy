using System.Net;

namespace HPPAQDeploy.Core.Interfaces;

public record RemoteProcessResult(int ExitCode, string Output, string ErrorOutput);

public interface IRemoteExecutor
{
    Task<RemoteProcessResult> ExecuteAsync(
        string hostname,
        NetworkCredential cred,
        string commandLine,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken ct,
        IProgress<string>? progress = null);

    /// <summary>
    /// Tests DCOM/WMI connectivity to a remote host by issuing a lightweight WMI query.
    /// </summary>
    Task<bool> TestConnectionAsync(
        string hostname,
        NetworkCredential credential,
        CancellationToken ct);
}

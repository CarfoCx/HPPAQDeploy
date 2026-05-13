using System.Net;

namespace HPPAQDeploy.Core.Interfaces;

public interface IAgentBootstrapper
{
    Task BootstrapAsync(string hostname, NetworkCredential credential, CancellationToken ct);

    Task RunOnceAsync(string hostname, NetworkCredential credential, CancellationToken ct);
}

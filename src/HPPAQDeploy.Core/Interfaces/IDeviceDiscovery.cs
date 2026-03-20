using System.Net;
using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.Core.Interfaces;

public interface IDeviceDiscovery
{
    Task<Device?> IdentifyDeviceAsync(string ipAddress, NetworkCredential credential, CancellationToken ct);
}

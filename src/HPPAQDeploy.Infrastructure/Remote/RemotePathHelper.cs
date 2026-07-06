using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.Infrastructure.Remote;

internal static class RemotePathHelper
{
    public static string GetTarget(Device device)
    {
        if (!string.IsNullOrWhiteSpace(device.Hostname))
            return device.Hostname.Trim();

        if (!string.IsNullOrWhiteSpace(device.IpAddress))
            return device.IpAddress.Trim();

        throw new InvalidOperationException("Device does not have a hostname or IP address.");
    }

    public static string ToUncPath(string hostname, string remotePath)
    {
        if (remotePath.Length >= 2 && remotePath[1] == ':')
            return $"\\\\{hostname}\\{remotePath[0]}${remotePath.Substring(2)}";

        return remotePath.StartsWith(@"\\", StringComparison.Ordinal)
            ? remotePath
            : $"\\\\{hostname}\\{remotePath}";
    }
}

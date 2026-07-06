using System.Management;
using System.Net;

namespace HPPAQDeploy.Infrastructure.Remote;

/// <summary>
/// Helper that creates a ManagementScope for a remote host with the given credentials.
/// </summary>
public static class WmiConnectionFactory
{
    /// <summary>
    /// Creates and connects a ManagementScope to \\hostname\root\cimv2.
    /// </summary>
    public static ManagementScope CreateScope(string hostname, NetworkCredential credential)
    {
        var options = new ConnectionOptions
        {
            Username = BuildUsername(credential),
            Password = credential.Password,
            Impersonation = ImpersonationLevel.Impersonate,
            Authentication = AuthenticationLevel.PacketPrivacy,
            EnablePrivileges = true,
            Timeout = TimeSpan.FromSeconds(HPPAQDeploy.Shared.Configuration.AppSettings.WmiTimeoutSeconds)
        };

        var path = $"\\\\{hostname}\\root\\cimv2";
        var scope = new ManagementScope(path, options);
        scope.Connect();

        return scope;
    }

    public static string BuildUsername(NetworkCredential credential)
    {
        if (string.IsNullOrWhiteSpace(credential.Domain))
            return credential.UserName;

        return $"{credential.Domain}\\{credential.UserName}";
    }
}

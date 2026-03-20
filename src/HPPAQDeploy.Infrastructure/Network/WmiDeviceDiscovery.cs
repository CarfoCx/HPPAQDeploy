using System.Management;
using System.Net;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Shared.Configuration;
using HPPAQDeploy.Shared.Helpers;
using Serilog;

namespace HPPAQDeploy.Infrastructure.Network;

/// <summary>
/// Implements IDeviceDiscovery using WMI/DCOM to query remote machine hardware info.
/// Returns null for non-HP devices.
/// </summary>
public class WmiDeviceDiscovery : IDeviceDiscovery
{
    private readonly ILogger _logger = Log.ForContext<WmiDeviceDiscovery>();

    public async Task<Device?> IdentifyDeviceAsync(
        string ipAddress,
        NetworkCredential credential,
        CancellationToken ct)
    {
        // No retries for bulk discovery — retrying 100+ hosts with 2-8s delays is too slow.
        // Single-host add still benefits from the timeout handling.
        return await IdentifyDeviceInternalAsync(ipAddress, credential, ct).ConfigureAwait(false);
    }

    private async Task<Device?> IdentifyDeviceInternalAsync(
        string ipAddress,
        NetworkCredential credential,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var options = new ConnectionOptions
            {
                Authentication = AuthenticationLevel.PacketPrivacy,
                Impersonation = ImpersonationLevel.Impersonate,
                Username = string.IsNullOrEmpty(credential.Domain)
                    ? credential.UserName
                    : $"{credential.Domain}\\{credential.UserName}",
                Password = credential.Password,
                Timeout = TimeSpan.FromSeconds(AppSettings.WmiTimeoutSeconds)
            };

            var scope = new ManagementScope($"\\\\{ipAddress}\\root\\cimv2", options);
            scope.Connect();

            if (!scope.IsConnected)
            {
                _logger.Warning("Failed to connect to WMI on {IpAddress}", ipAddress);
                return null;
            }

            // Query Win32_ComputerSystem
            string manufacturer = string.Empty;
            string model = string.Empty;
            string hostname = string.Empty;

            using (var csSearcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Manufacturer, Model, Name FROM Win32_ComputerSystem")))
            {
                foreach (var obj in csSearcher.Get())
                {
                    manufacturer = obj["Manufacturer"]?.ToString() ?? string.Empty;
                    model = obj["Model"]?.ToString() ?? string.Empty;
                    hostname = obj["Name"]?.ToString() ?? string.Empty;
                }
            }

            // Filter: only HP devices
            if (!IsHpDevice(manufacturer))
            {
                _logger.Information("Device at {IpAddress} is not HP (Manufacturer={Manufacturer}), skipping",
                    ipAddress, manufacturer);
                return null;
            }

            // Query Win32_BaseBoard
            string productId = string.Empty;
            using (var bbSearcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Product FROM Win32_BaseBoard")))
            {
                foreach (var obj in bbSearcher.Get())
                {
                    productId = obj["Product"]?.ToString() ?? string.Empty;
                }
            }

            // Query Win32_BIOS
            string serialNumber = string.Empty;
            string biosVersion = string.Empty;
            using (var biosSearcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT SerialNumber, SMBIOSBIOSVersion FROM Win32_BIOS")))
            {
                foreach (var obj in biosSearcher.Get())
                {
                    serialNumber = obj["SerialNumber"]?.ToString() ?? string.Empty;
                    biosVersion = obj["SMBIOSBIOSVersion"]?.ToString() ?? string.Empty;
                }
            }

            // Query Win32_OperatingSystem
            string osCaption = string.Empty;
            string osVersion = string.Empty;
            using (var osSearcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Caption, Version FROM Win32_OperatingSystem")))
            {
                foreach (var obj in osSearcher.Get())
                {
                    osCaption = obj["Caption"]?.ToString() ?? string.Empty;
                    osVersion = obj["Version"]?.ToString() ?? string.Empty;
                }
            }

            var device = new Device
            {
                Hostname = hostname,
                IpAddress = ipAddress,
                Manufacturer = manufacturer,
                Model = model,
                SerialNumber = serialNumber,
                ProductId = productId,
                OsVersion = $"{osCaption} ({osVersion})",
                BiosVersion = biosVersion,
                Status = DeviceStatus.Online,
                LastScanned = DateTime.UtcNow
            };

            _logger.Information("Discovered HP device: {Hostname} ({Model}) at {IpAddress}",
                hostname, model, ipAddress);

            return device;
        }, ct).ConfigureAwait(false);
    }

    private static bool IsHpDevice(string manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer))
            return false;

        var upper = manufacturer.ToUpperInvariant();
        return upper.Contains("HP") || upper.Contains("HEWLETT");
    }
}

using System.Net;
using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.Infrastructure.Network;

/// <summary>
/// Static helper for CIDR-related calculations.
/// The main CIDR logic lives in <see cref="CidrRange"/> in Core.
/// This class provides supplementary utilities.
/// </summary>
public static class CidrCalculator
{
    /// <summary>
    /// Validates whether the given string is a valid CIDR notation.
    /// </summary>
    public static bool IsValidCidr(string cidr)
    {
        if (string.IsNullOrWhiteSpace(cidr))
            return false;

        var parts = cidr.Split('/');
        if (parts.Length != 2)
            return false;

        if (!IPAddress.TryParse(parts[0], out _))
            return false;

        if (!int.TryParse(parts[1], out var prefix))
            return false;

        return prefix >= 0 && prefix <= 32;
    }

    /// <summary>
    /// Returns the subnet mask for a given prefix length.
    /// </summary>
    public static IPAddress PrefixToSubnetMask(int prefixLength)
    {
        if (prefixLength < 0 || prefixLength > 32)
            throw new ArgumentOutOfRangeException(nameof(prefixLength));

        uint mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
        var bytes = new byte[]
        {
            (byte)(mask >> 24),
            (byte)(mask >> 16),
            (byte)(mask >> 8),
            (byte)mask
        };
        return new IPAddress(bytes);
    }

    /// <summary>
    /// Determines whether an IP address falls within a CIDR range.
    /// </summary>
    public static bool IsInRange(IPAddress address, CidrRange range)
    {
        var addrBytes = address.GetAddressBytes();
        uint addrUint = (uint)(addrBytes[0] << 24 | addrBytes[1] << 16 | addrBytes[2] << 8 | addrBytes[3]);

        var startBytes = range.StartAddress.GetAddressBytes();
        uint startUint = (uint)(startBytes[0] << 24 | startBytes[1] << 16 | startBytes[2] << 8 | startBytes[3]);

        var endBytes = range.EndAddress.GetAddressBytes();
        uint endUint = (uint)(endBytes[0] << 24 | endBytes[1] << 16 | endBytes[2] << 8 | endBytes[3]);

        return addrUint >= startUint && addrUint <= endUint;
    }
}

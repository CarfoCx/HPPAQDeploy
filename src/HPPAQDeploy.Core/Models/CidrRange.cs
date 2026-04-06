using System.Net;

namespace HPPAQDeploy.Core.Models;

public class CidrRange
{
    public string Network { get; }
    public IPAddress StartAddress { get; }
    public IPAddress EndAddress { get; }
    public int TotalHosts { get; }

    private readonly uint _networkUint;
    private readonly uint _broadcastUint;

    public CidrRange(string cidrNotation)
    {
        Network = cidrNotation ?? throw new ArgumentNullException(nameof(cidrNotation));

        var parts = cidrNotation.Split('/');
        if (parts.Length != 2)
            throw new FormatException($"Invalid CIDR notation: {cidrNotation}");

        if (!IPAddress.TryParse(parts[0], out var ipAddress))
            throw new FormatException($"Invalid IP address: {parts[0]}");

        if (!int.TryParse(parts[1], out var prefixLength) || prefixLength < 0 || prefixLength > 32)
            throw new FormatException($"Invalid prefix length: {parts[1]}");

        var ipBytes = ipAddress.GetAddressBytes();
        uint ipUint = (uint)(ipBytes[0] << 24 | ipBytes[1] << 16 | ipBytes[2] << 8 | ipBytes[3]);

        uint mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
        _networkUint = ipUint & mask;
        _broadcastUint = _networkUint | ~mask;

        StartAddress = UintToIp(_networkUint);
        EndAddress = UintToIp(_broadcastUint);

        // Total usable hosts: exclude network and broadcast for prefixes <= 30
        if (prefixLength <= 30)
        {
            TotalHosts = (int)(_broadcastUint - _networkUint + 1);
        }
        else if (prefixLength == 31)
        {
            // Point-to-point link per RFC 3021
            TotalHosts = 2;
        }
        else
        {
            // /32 - single host
            TotalHosts = 1;
        }
    }

    public IEnumerable<IPAddress> GetAllHosts()
    {
        // For /31 and /32, return all addresses
        // For wider ranges, skip network address and broadcast address
        if (_broadcastUint - _networkUint <= 1)
        {
            for (uint i = _networkUint; i <= _broadcastUint; i++)
            {
                yield return UintToIp(i);
            }
        }
        else
        {
            // Skip network (_networkUint) and broadcast (_broadcastUint)
            for (uint i = _networkUint + 1; i < _broadcastUint; i++)
            {
                yield return UintToIp(i);
            }
        }
    }

    public bool Contains(string ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var ip)) return false;
        var bytes = ip.GetAddressBytes();
        uint ipUint = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        return ipUint >= _networkUint && ipUint <= _broadcastUint;
    }

    private static IPAddress UintToIp(uint value)
    {
        var bytes = new byte[]
        {
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        };
        return new IPAddress(bytes);
    }
}

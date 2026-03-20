using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.Tests;

public class CidrRangeTests
{
    [Fact]
    public void Slash24_Returns254_UsableHosts()
    {
        var cidr = new CidrRange("192.168.1.0/24");
        Assert.Equal(256, cidr.TotalHosts);

        var hosts = cidr.GetAllHosts().ToList();
        // /24 skips network (.0) and broadcast (.255) = 254 usable
        Assert.Equal(254, hosts.Count);
        Assert.Equal("192.168.1.1", hosts.First().ToString());
        Assert.Equal("192.168.1.254", hosts.Last().ToString());
    }

    [Fact]
    public void Slash32_Returns_SingleHost()
    {
        var cidr = new CidrRange("10.0.0.5/32");
        Assert.Equal(1, cidr.TotalHosts);

        var hosts = cidr.GetAllHosts().ToList();
        Assert.Single(hosts);
        Assert.Equal("10.0.0.5", hosts[0].ToString());
    }

    [Fact]
    public void Slash31_Returns_TwoHosts()
    {
        var cidr = new CidrRange("10.0.0.4/31");
        Assert.Equal(2, cidr.TotalHosts);

        var hosts = cidr.GetAllHosts().ToList();
        Assert.Equal(2, hosts.Count);
    }

    [Fact]
    public void InvalidCidr_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => new CidrRange("not-an-ip"));
        Assert.Throws<FormatException>(() => new CidrRange("192.168.1.0"));
        Assert.Throws<FormatException>(() => new CidrRange("192.168.1.0/33"));
    }

    [Fact]
    public void StartAndEndAddresses_AreCorrect()
    {
        var cidr = new CidrRange("172.16.0.0/16");
        Assert.Equal("172.16.0.0", cidr.StartAddress.ToString());
        Assert.Equal("172.16.255.255", cidr.EndAddress.ToString());
    }
}

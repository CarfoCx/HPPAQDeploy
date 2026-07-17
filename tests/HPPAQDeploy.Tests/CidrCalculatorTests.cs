using System.Net;
using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Infrastructure.Network;

namespace HPPAQDeploy.Tests;

public class CidrCalculatorTests
{
    [Theory]
    [InlineData("10.0.0.0/24", true)]
    [InlineData("0.0.0.0/0", false)]
    [InlineData("0.0.0.0/1", false)]
    [InlineData("2001:db8::/32", false)]
    public void IsValidCidr_MatchesSupportedCidrRangeInputs(string input, bool expected)
    {
        Assert.Equal(expected, CidrCalculator.IsValidCidr(input));
        if (expected)
            _ = new CidrRange(input);
        else
            Assert.ThrowsAny<Exception>(() => new CidrRange(input));
    }

    [Fact]
    public void IsInRange_ReturnsFalseForIpv6()
    {
        Assert.False(CidrCalculator.IsInRange(
            IPAddress.Parse("2001:db8::1"),
            new CidrRange("10.0.0.0/24")));
    }
}

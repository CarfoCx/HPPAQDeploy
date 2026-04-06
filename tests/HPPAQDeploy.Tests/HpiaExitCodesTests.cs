using HPPAQDeploy.Shared.Helpers;

namespace HPPAQDeploy.Tests;

public class HpiaExitCodesTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(256, true)]
    [InlineData(3010, true)]
    [InlineData(3020, true)]
    [InlineData(4096, false)]
    [InlineData(4097, false)]
    [InlineData(-1, false)]
    [InlineData(9999, false)]
    public void IsSuccess_ReturnsExpected(int exitCode, bool expected)
    {
        Assert.Equal(expected, HpiaExitCodes.IsSuccess(exitCode));
    }

    [Theory]
    [InlineData(3010, true)]
    [InlineData(3020, true)]
    [InlineData(0, false)]
    [InlineData(256, false)]
    [InlineData(4096, false)]
    public void RequiresReboot_ReturnsExpected(int exitCode, bool expected)
    {
        Assert.Equal(expected, HpiaExitCodes.RequiresReboot(exitCode));
    }

    [Fact]
    public void GetMessage_KnownCode_ReturnsDescription()
    {
        Assert.Equal("Success", HpiaExitCodes.GetMessage(0));
        Assert.Equal("Success - reboot required", HpiaExitCodes.GetMessage(3010));
    }

    [Fact]
    public void GetMessage_UnknownCode_ReturnsUnknown()
    {
        var msg = HpiaExitCodes.GetMessage(12345);
        Assert.Contains("Unknown", msg);
        Assert.Contains("12345", msg);
    }
}

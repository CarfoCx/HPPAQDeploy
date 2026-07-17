using HPPAQDeploy.Shared.Configuration;

namespace HPPAQDeploy.Tests;

public class AppSettingsTests
{
    [Fact]
    public void ProtectString_UnprotectString_RoundTrip()
    {
        var original = "MySecretP@ssw0rd!";
        var encrypted = AppSettings.ProtectString(original);

        Assert.NotEqual(original, encrypted);
        Assert.NotEmpty(encrypted);

        var decrypted = AppSettings.UnprotectString(encrypted);
        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void ProtectString_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal("", AppSettings.ProtectString(""));
        Assert.Equal("", AppSettings.ProtectString(null!));
    }

    [Fact]
    public void UnprotectString_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal("", AppSettings.UnprotectString(""));
        Assert.Equal("", AppSettings.UnprotectString(null!));
    }

    [Fact]
    public void UnprotectString_PlaintextFallback_ReturnsSameValue()
    {
        // For backward compatibility, non-DPAPI strings should be returned as-is
        var plaintext = "not-encrypted-password";
        var result = AppSettings.UnprotectString(plaintext);
        Assert.Equal(plaintext, result);
    }

    [Theory]
    [InlineData(double.NaN, 24)]
    [InlineData(double.PositiveInfinity, 24)]
    [InlineData(-10, 1)]
    [InlineData(100000, 8760)]
    public void ScheduledScanInterval_IsFiniteAndBounded(double input, double expectedHours)
    {
        Assert.Equal(expectedHours, AppSettings.NormalizeScheduledScanInterval(input).TotalHours);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(587, 587)]
    [InlineData(99999, 65535)]
    public void SmtpPort_IsBounded(int input, int expected)
    {
        Assert.Equal(expected, AppSettings.NormalizeSmtpPort(input));
    }
}

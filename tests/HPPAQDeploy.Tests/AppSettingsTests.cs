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
}

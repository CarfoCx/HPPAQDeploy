using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.Tests;

public class ModelEqualityTests
{
    [Fact]
    public void UnsavedDevices_AreNotEqualMerelyBecauseIdsAreZero()
    {
        Assert.NotEqual(new Device(), new Device());
    }

    [Fact]
    public void PersistedDevices_WithSameIdAreEqual()
    {
        Assert.Equal(new Device { Id = 42 }, new Device { Id = 42 });
    }

    [Fact]
    public void UnsavedCredentials_AreNotEqualMerelyBecauseIdsAreZero()
    {
        Assert.NotEqual(new Credential(), new Credential());
    }
}

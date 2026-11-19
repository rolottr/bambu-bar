using BambuBar.Core.App;

namespace BambuBar.Core.Tests;

/// <summary>
/// A printer's two secrets are separate items: its access code and the password of an external camera entered
/// for it. CredentialStore, the real store, lives in BambuBar.Windows, which no test project references, so
/// these pin the contract on the in-memory store every other implementation follows.
/// </summary>
public class SecretStoreTests
{
    private const string Serial = "01P00C000000001";

    [Fact]
    public void ACameraPasswordRoundTrips()
    {
        var secrets = new MemorySecretStore();
        secrets.SetCameraPassword("cam-secret", Serial);
        Assert.Equal("cam-secret", secrets.CameraPassword(Serial));
    }

    [Fact]
    public void ACameraPasswordIsIndependentOfTheAccessCode()
    {
        var secrets = new MemorySecretStore();
        secrets.SetCameraPassword("cam-secret", Serial);
        secrets.SetAccessCode("12345678", Serial);
        Assert.Equal("cam-secret", secrets.CameraPassword(Serial));
        Assert.Equal("12345678", secrets.AccessCode(Serial));

        secrets.SetCameraPassword("other", Serial);
        Assert.Equal("other", secrets.CameraPassword(Serial));
        Assert.Equal("12345678", secrets.AccessCode(Serial));
    }

    [Fact]
    public void RemovingTheCameraPasswordKeepsTheAccessCode()
    {
        var secrets = new MemorySecretStore();
        secrets.SetCameraPassword("cam-secret", Serial);
        secrets.SetAccessCode("12345678", Serial);
        secrets.RemoveCameraPassword(Serial);
        Assert.Null(secrets.CameraPassword(Serial));
        Assert.Equal("12345678", secrets.AccessCode(Serial));
    }

    [Fact]
    public void NoCameraPasswordIsNull()
    {
        Assert.Null(new MemorySecretStore().CameraPassword(Serial));
    }
}

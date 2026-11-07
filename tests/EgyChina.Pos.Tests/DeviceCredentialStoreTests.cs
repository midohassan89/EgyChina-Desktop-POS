using EgyChina.Pos.Data;
using EgyChina.Pos.Sync;
using Xunit;

namespace EgyChina.Pos.Tests;

[Collection("PosPathsSerial")]
public sealed class DeviceCredentialStoreTests : IDisposable
{
    private readonly string _tempRoot;

    public DeviceCredentialStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "egychina-pos-cred", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        PosPaths.SetRootOverrideForTests(_tempRoot);
    }

    public void Dispose()
    {
        PosPaths.SetRootOverrideForTests(null);
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Save_load_roundtrip_and_clear()
    {
        DeviceCredentialStore.Clear();
        Assert.Null(DeviceCredentialStore.Load());

        DeviceCredentialStore.Save("secret-device-token-001");
        Assert.Equal("secret-device-token-001", DeviceCredentialStore.Load());
        Assert.True(File.Exists(DeviceCredentialStore.CredentialPath()));

        DeviceCredentialStore.Clear();
        Assert.Null(DeviceCredentialStore.Load());
        Assert.False(File.Exists(DeviceCredentialStore.CredentialPath()));
    }

    [Fact]
    public void Cookie_env_alone_does_not_enable_transmit()
    {
        var options = new SyncOptions
        {
            BaseAddress = new Uri("https://erp.echinamart.com/"),
            DeviceToken = null,
            PaymentMethod = "cash",
            MoneyScale = ErpMoneyScale.MajorUnits,
        };
        Assert.Equal("auth-missing", options.TransmissionBlock());
        Assert.False(options.CanTransmit);
    }
}

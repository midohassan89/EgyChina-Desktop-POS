using EgyChina.Pos.Core;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class PosUpdateSettingsTests : IDisposable
{
    private readonly string _directory;

    public PosUpdateSettingsTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "egychina-pos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Environment.SetEnvironmentVariable(PosUpdateSettings.UpdateUrlVariable, null);
        Environment.SetEnvironmentVariable(PosUpdateSettings.UpdateChannelVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PosUpdateSettings.UpdateUrlVariable, null);
        Environment.SetEnvironmentVariable(PosUpdateSettings.UpdateChannelVariable, null);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Default_channel_is_stable_feed()
    {
        var settings = PosUpdateSettings.Load(_directory);
        Assert.Equal("stable", settings.Channel);
        Assert.Equal("https://updates.echinamart.com/stable/", settings.FeedUrl.ToString());
    }

    [Fact]
    public void File_channel_beta_is_honored()
    {
        PosUpdateSettings.WriteChannel(_directory, "beta");
        var settings = PosUpdateSettings.Load(_directory);
        Assert.Equal("beta", settings.Channel);
        Assert.Equal("https://updates.echinamart.com/beta/", settings.FeedUrl.ToString());
    }

    [Fact]
    public void Env_url_overrides_channel_file()
    {
        PosUpdateSettings.WriteChannel(_directory, "stable");
        Environment.SetEnvironmentVariable(
            PosUpdateSettings.UpdateUrlVariable,
            "https://updates.echinamart.com/beta/");
        var settings = PosUpdateSettings.Load(_directory);
        Assert.Equal("https://updates.echinamart.com/beta/", settings.FeedUrl.ToString());
        Assert.Equal("beta", settings.Channel);
    }

    [Fact]
    public void Env_channel_overrides_file()
    {
        PosUpdateSettings.WriteChannel(_directory, "stable");
        Environment.SetEnvironmentVariable(PosUpdateSettings.UpdateChannelVariable, "beta");
        var settings = PosUpdateSettings.Load(_directory);
        Assert.Equal("beta", settings.Channel);
        Assert.Equal("https://updates.echinamart.com/beta/", settings.FeedUrl.ToString());
    }
}

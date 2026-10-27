using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

public sealed class RemoteSettingsTests
{
    [Fact]
    public void SettingsPersistHostsAndSelection()
    {
        var host = new RemoteHost("build", "Build Server", "build-server", "/opt/collector");
        var settings = AppSettings.Default with { Remote = new() { Hosts = [host], Selection = "all" } };
        var loaded = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));
        Assert.Equal("all", loaded.Remote.Selection);
        Assert.Equal(host, Assert.Single(loaded.Remote.Hosts));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"remote\":null}")]
    [InlineData("{\"remote\":{\"hosts\":null}}")]
    [InlineData("{\"remote\":{\"selection\":\"missing\"}}")]
    public void OldOrInvalidSettingsStayLocal(string json)
    {
        var loaded = SettingsSerializer.Deserialize(json);
        Assert.Equal("local", loaded.Remote.Selection);
        Assert.Empty(loaded.Remote.Hosts);
    }
}

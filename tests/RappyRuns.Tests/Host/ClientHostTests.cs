using RappyRuns.Host;

namespace RappyRuns.Tests.Host;

/// <summary>A whole <see cref="ClientHost"/> built from fakes (S32).</summary>
public sealed class ClientHostTests
{
    [Fact(DisplayName = "host: builds from fake services, answers hello, and reads autostart from the seam (S32)")]
    public void BuildsFromFakes()
    {
        using var h = new HostHarness();
        h.Services.AutostartSetting.Enabled = true;
        h.CallOrdered("app.hello");
        var (name, json) = Assert.Single(h.Sink.Events);
        Assert.Equal("reply:app.hello", name);
        Assert.Contains("\"version\"", json, StringComparison.Ordinal);
        // Seeded at construction, before the fake was switched on.
        Assert.False(h.Host.Ui.Settings.Autostart);
        Assert.True(h.Host.SetAutostart(true));
        Assert.True(h.Host.Ui.Settings.Autostart);
        Assert.True(h.Services.AutostartSetting.Enabled);
    }
}

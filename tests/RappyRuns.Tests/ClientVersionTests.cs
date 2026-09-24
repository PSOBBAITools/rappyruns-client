using RappyRuns.Core;

namespace RappyRuns.Tests;

// Port of the version checks in run-updater-tests (client/tests/tests-helpers.lisp).
public class ClientVersionTests
{
    [Fact]
    public void ReadsVPrefixedTags() => Assert.Equal([1, 2, 3], ClientVersion.Parse("v1.2.3")!);

    [Fact]
    public void ReadsBareVersions() => Assert.Equal([0, 10, 0], ClientVersion.Parse("0.10.0")!);

    [Theory]
    [InlineData("v1.2")]
    [InlineData("v1.2.3.4")]
    [InlineData("v1.2.3-rc1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("v..")]
    [InlineData(null)]
    public void RejectsAnythingElse(string? text) => Assert.Null(ClientVersion.Parse(text));

    [Fact]
    public void ComparesNumericallyNotTextually() => Assert.True(ClientVersion.UpdateAvailable("0.9.0", "v0.10.0"));

    [Fact]
    public void OfferNewerRelease() => Assert.True(ClientVersion.UpdateAvailable("0.5.0", "v0.6.0"));

    [Fact]
    public void SameVersionIsNotAnUpdate() => Assert.False(ClientVersion.UpdateAvailable("0.6.0", "v0.6.0"));

    [Fact]
    public void DevBuildNeverUpdates() => Assert.False(ClientVersion.UpdateAvailable(null, "v0.6.0"));

    [Fact]
    public void MalformedTagIsNotAnUpdate() => Assert.False(ClientVersion.UpdateAvailable("0.5.0", "release-2"));

    [Fact]
    public void TestBuildHasNoBakedVersion() => Assert.Null(ClientVersion.Current);
}

using System.Text;
using RappyRuns.Core.Config;
using static RappyRuns.Tests.Store.StoreTestSupport;

namespace RappyRuns.Tests.Store;

/// <summary>credentials.lisp: tests-misc.lisp run-credentials-tests, plus the SBCL golden.</summary>
public class CredentialsTests
{
    [Fact]
    public void PlainUsernameAndPasswordLinesParse()
    {
        var c = Credentials.Parse("username=Teapot\npassword=secret123\n");
        Assert.Equal(new LoginCredentials("Teapot", "secret123"), c);
    }

    [Fact]
    public void BomCrlfCommentsAndSpacesAroundKeysAreTolerated()
    {
        var c = Credentials.Parse("﻿# comment\r\n  username = Teapot \r\npassword=a=b=c\r\n");
        Assert.Equal(new LoginCredentials("Teapot", "a=b=c"), c);
    }

    [Theory]
    [InlineData("a missing password yields NIL NIL", "username=Teapot\n")]
    [InlineData("empty values yield NIL NIL", "username=Teapot\npassword=\n")]
    [InlineData("empty text yields NIL NIL", "")]
    [InlineData("NIL text yields NIL NIL", null)]
    public void IncompleteInputYieldsNothing(string label, string? text) =>
        Assert.True(Credentials.Parse(text) is null, label);

    [Fact]
    public void ReadCredentialsReadsARealFile()
    {
        using var dir = new TempDir("rr-store-test");
        var path = Credentials.PathIn(dir.Path);
        File.WriteAllText(path, "username=Teapot\npassword=secret123\n", new UTF8Encoding(false));
        Assert.Equal(new LoginCredentials("Teapot", "secret123"), Credentials.Read(path));
        Assert.True(Credentials.Present(dir.Path));
    }

    [Fact]
    public void ReadCredentialsOnAMissingFileYieldsNilNil()
    {
        using var dir = new TempDir("rr-store-test");
        Assert.Null(Credentials.Read(Credentials.PathIn(dir.Path)));
        Assert.False(Credentials.Present(dir.Path));
    }

    [Fact]
    public void ANonUtf8FileYieldsNothing()
    {
        using var dir = new TempDir("rr-store-test");
        var path = Credentials.PathIn(dir.Path);
        // "username=" + Shift-JIS bytes (invalid UTF-8).
        File.WriteAllBytes(path, [.. "username=u\npassword="u8.ToArray(), 0x83, 0x65, 0x83, 0x58, 0x83, 0x67]);
        Assert.Null(Credentials.Read(path));
    }

    [Fact]
    public void MatchesTheLispParserOnEveryGoldenCase()
    {
        foreach (var g in Golden.Load("store/credentials.json").EnumerateArray())
        {
            var text = g.GetProperty("text").GetString();
            var parsed = Credentials.Parse(text);
            Assert.True(g.GetProperty("username").GetString() == parsed?.Username, $"username for {text}");
            Assert.True(g.GetProperty("password").GetString() == parsed?.Password, $"password for {text}");
        }
    }
}

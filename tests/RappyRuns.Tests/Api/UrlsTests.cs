using System.Text.Json;
using RappyRuns.Core.Api;

namespace RappyRuns.Tests.Api;

/// <summary>URL helpers (tests-helpers.lisp run-pure-helper-tests, tests-ghost, tests-misc, tests-pinshare).</summary>
public class UrlsTests
{
    [Fact(DisplayName = "parse-url https defaults")]
    public void ParseUrlHttpsDefaults() =>
        Assert.Equal(new UrlParts("https", "example.com", 443, "/api/quests"), Urls.Parse("https://example.com/api/quests"));

    [Fact(DisplayName = "parse-url explicit port, bare authority")]
    public void ParseUrlExplicitPort() =>
        Assert.Equal(new UrlParts("http", "localhost", 8123, "/"), Urls.Parse("http://localhost:8123"));

    [Fact(DisplayName = "parse-url schemeless URL signals api-error")]
    public void ParseUrlSchemeless()
    {
        var ex = Assert.Throws<ApiException>(() => Urls.Parse("example.com/x"));
        Assert.StartsWith("Bad URL: ", ex.Message);
    }

    [Fact(DisplayName = "parse-url junk port signals a non-api error (pinned)")]
    public void ParseUrlJunkPort()
    {
        var ex = Record.Exception(() => Urls.Parse("http://h:abc/"));
        Assert.NotNull(ex);
        Assert.IsNotType<ApiException>(ex);
    }

    [Fact(DisplayName = "parse-url lower-cases the scheme and keeps the query in the path")]
    public void ParseUrlSchemeCaseAndQuery() =>
        Assert.Equal(new UrlParts("https", "Host", 443, "/p?x=1"), Urls.Parse("HTTPS://Host/p?x=1"));

    [Fact(DisplayName = "ws / wss URLs get the right default ports")]
    public void WebSocketDefaultPorts()
    {
        Assert.Equal(new WebSocketUrlParts(true, "relay.example", 443, "/ws"), Urls.ParseWebSocket("wss://relay.example/ws"));
        Assert.Equal(new WebSocketUrlParts(false, "localhost", 8787, "/"), Urls.ParseWebSocket("ws://localhost:8787"));
        Assert.Throws<ApiException>(() => Urls.ParseWebSocket("https://x/"));
    }

    [Fact(DisplayName = "http and https URLs are openable")]
    public void HttpUrlsOpenable()
    {
        Assert.True(Urls.IsValidHttpUrl("http://x/y"));
        Assert.True(Urls.IsValidHttpUrl("https://x/y"));
    }

    [Theory(DisplayName = "non-web strings are rejected")]
    [InlineData("C:\\evil.exe")]
    [InlineData("file:///c:/x")]
    [InlineData("https://x/a b")]
    [InlineData("javascript:alert(1)")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://x/\ty")]
    public void NonWebStringsRejected(string? url) => Assert.False(Urls.IsValidHttpUrl(url));

    [Fact(DisplayName = "url encoding passes unreserved and encodes spaces")]
    public void EncodeSpaces() => Assert.Equal("Very%20Hard", Urls.EncodeComponent("Very Hard"));

    [Fact(DisplayName = "url encoding is utf-8 percent-encoded")]
    public void EncodeUtf8() => Assert.Equal("A%E3%81%82", Urls.EncodeComponent("Aあ"));

    [Fact(DisplayName = "url-encode-component matches the Lisp golden")]
    public void EncodeGolden()
    {
        foreach (var c in Golden.Load("api/api-golden.json").GetProperty("encode").EnumerateArray())
            Assert.Equal(c.GetProperty("out").GetString(), Urls.EncodeComponent(c.GetProperty("in").GetString()!));
    }

    [Fact(DisplayName = "video-file url carries the offset when known")]
    public void VideoFileOffset() => Assert.Equal("/api/runs/7/video-file?offset_ms=1234", Urls.VideoFilePath(7, 1234));

    [Fact(DisplayName = "video-file url omits the offset when unknown")]
    public void VideoFileNoOffset() => Assert.Equal("/api/runs/7/video-file", Urls.VideoFilePath(7, null));

    [Theory(DisplayName = "api-url trims every trailing slash of the server URL")]
    [InlineData("https://s.example", "/api/me", "https://s.example/api/me")]
    [InlineData("https://s.example/", "/api/me", "https://s.example/api/me")]
    [InlineData("https://s.example//", "/api/me", "https://s.example/api/me")]
    [InlineData("http://localhost:8080/base/", "/api/me", "http://localhost:8080/base/api/me")]
    public void ApiUrlTrims(string server, string path, string expected) => Assert.Equal(expected, Urls.ApiUrl(server, path));

    [Fact(DisplayName = "pairing url sends the browser to /pair?code=")]
    public void PairingUrl() => Assert.Equal("https://s.example/pair?code=AB12", Urls.PairingUrl("https://s.example/", "AB12"));

    private static List<string> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToList();

    private static int? Int(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt32();

    [Fact(DisplayName = "ghost query paths match fetch-ghost-splits (golden)")]
    public void GhostPathGolden()
    {
        foreach (var c in Golden.Load("api/api-golden.json").GetProperty("ghost").EnumerateArray())
        {
            var difficulty = c.GetProperty("difficulty");
            var path = Urls.GhostPath(c.GetProperty("slug").GetString()!, Strings(c.GetProperty("extra")),
                difficulty.ValueKind == JsonValueKind.Null ? null : difficulty.GetString(),
                Int(c.GetProperty("party_size")), Int(c.GetProperty("pb")));
            Assert.Equal(c.GetProperty("path").GetString(), Urls.ApiUrl("https://s.example/", path));
        }
    }

    [Fact(DisplayName = "pin set paths match fetch-pin-set (golden)")]
    public void PinsPathGolden()
    {
        foreach (var c in Golden.Load("api/api-golden.json").GetProperty("pins").EnumerateArray())
        {
            var path = Urls.PinsPath(c.GetProperty("slug").GetString()!, Strings(c.GetProperty("extra")));
            Assert.Equal(c.GetProperty("path").GetString(), Urls.ApiUrl("https://s.example", path));
        }
    }
}

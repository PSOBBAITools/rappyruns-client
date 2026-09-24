using RappyRuns.Core.Api;
using RappyRuns.Core.Game;
using RappyRuns.Core.Sexp;
using RappyRuns.Core.Store;
using RappyRuns.Core.Update;
using RappyRuns.Host;
using RappyRuns.Tests.Api;

namespace RappyRuns.Tests.Host;

/// <summary>Composition-root failure paths: bad data files, unencodable runs, the %TEMP% contracts.</summary>
public sealed class HostRobustnessTests : IDisposable
{
    private readonly TempDir _dir = new("rr-hostfix");

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Fact(DisplayName = "only the upload's own failures count toward giving it up (S17)")]
    public void UploadFailureCounting()
    {
        Assert.True(QueueNetwork.CountsAgainstUpload(new ApiException("POST ... -> 502: x") { Status = 502 }));
        Assert.True(QueueNetwork.CountsAgainstUpload(new ApiException("reset", TransportFailure.Other) { BodyStarted = true }));
        Assert.True(QueueNetwork.CountsAgainstUpload(new ApiException("timed out", TransportFailure.Timeout) { BodyStarted = true }));
        Assert.False(QueueNetwork.CountsAgainstUpload(ApiException.InvalidToken()));
        Assert.False(QueueNetwork.CountsAgainstUpload(new ApiException("dns", TransportFailure.AddressNotFound)));
        Assert.False(QueueNetwork.CountsAgainstUpload(new ApiException("refused", TransportFailure.ConnectFailed)));
        Assert.False(QueueNetwork.CountsAgainstUpload(new ApiException("file too large to upload (5 bytes)")));
        Assert.False(QueueNetwork.CountsAgainstUpload(new IOException("locked")));
    }

    [Theory(DisplayName = "a malformed or missing quest-triggers.sexp is logged, never fatal")]
    [InlineData("((:slug \"a\" :episode 1", true)]         // unterminated list (SexpException)
    [InlineData("(:slug \"a\")", false)]                     // a plist, not a list of plists
    [InlineData("((:slug \"a\" :names 5 :start 1))", false)] // wrong shapes inside
    [InlineData(null, true)]                                  // no file at all
    public void MalformedBuiltinTriggers(string? text, bool mustFail)
    {
        var path = Path.Combine(_dir.Path, "quest-triggers.sexp");
        if (text is not null) File.WriteAllText(path, text);
        var catalog = new QuestCatalog();
        var log = new List<string>();
        var loaded = ClientHost.LoadBuiltinQuests(catalog, path, log.Add);
        if (mustFail) Assert.False(loaded);
        if (!loaded)
        {
            Assert.Contains(log, l => l.StartsWith("quest-triggers.sexp could not be read", StringComparison.Ordinal));
            Assert.Empty(catalog.Builtin);
        }
    }

    [Fact(DisplayName = "a run that cannot be encoded fails alone with the reason; the pass goes on")]
    public async Task UnencodableRunFailsAlone()
    {
        var config = RappyRuns.Core.Config.ConfigStore.Open(_dir.Path);
        config.ServerUrl = "https://s.example";
        config.AnonToken = "anon-1";
        var server = new FakeHandler(_ => (201, """{"id":7,"url":"https://s.example/runs/7"}"""));
        var api = new ApiClient(new HttpTransport(server), new ConfigAuthSettings(config));
        var network = new QueueNetwork(api,
            run => run.Get("QUEST-SLUG")?.AsString == "bad" ? throw new InvalidCastException("not a number") : "{}",
            () => "");
        var queue = new RunQueue(config.QueuePath);
        queue.Enqueue(new Plist().With("QUEST-SLUG", "good"));
        queue.Enqueue(new Plist().With("QUEST-SLUG", "bad")); // newest first: submitted first

        var results = await queue.SubmitQueuedAsync(config, network, network, "PC");

        Assert.NotNull(results);
        var bad = Assert.Single(queue.Entries, e => e.QuestSlug == "bad");
        Assert.Equal(RunStatus.Failed, bad.Status);
        Assert.Contains("could not encode the run", bad.Get(RunKeys.Reason).AsString);
        Assert.Equal(RunStatus.Submitted, Assert.Single(queue.Entries, e => e.QuestSlug == "good").Status);
        Assert.Single(server.Requests);
    }

    [Fact(DisplayName = "the startup marker lives in windows-temp-dir (TEMP first), where the bridge helper reads it")]
    public void StartupMarkerInWindowsTempDir() =>
        Assert.Equal(Path.Combine(UpdateFiles.TempDir(), "rappyruns-client-started.txt"), StartupMarker.FilePath);
}

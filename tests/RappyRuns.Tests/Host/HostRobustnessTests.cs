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
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rr-hostfix-" + Guid.NewGuid().ToString("N"));

    public HostRobustnessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [Theory(DisplayName = "a malformed or missing quest-triggers.sexp is logged, never fatal")]
    [InlineData("((:slug \"a\" :episode 1", true)]         // unterminated list (SexpException)
    [InlineData("(:slug \"a\")", false)]                     // a plist, not a list of plists
    [InlineData("((:slug \"a\" :names 5 :start 1))", false)] // wrong shapes inside
    [InlineData(null, true)]                                  // no file at all
    public void MalformedBuiltinTriggers(string? text, bool mustFail)
    {
        var path = Path.Combine(_dir, "quest-triggers.sexp");
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
        var config = RappyRuns.Core.Config.ConfigStore.Open(_dir);
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

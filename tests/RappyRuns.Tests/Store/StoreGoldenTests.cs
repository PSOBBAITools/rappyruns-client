using RappyRuns.Core.Config;
using RappyRuns.Core.I18n;
using RappyRuns.Core.Store;
using static RappyRuns.Tests.Store.StoreTestSupport;

namespace RappyRuns.Tests.Store;

/// <summary>
/// File compatibility with the Lisp client (spec core §3.4): queue.sexp and
/// config.sexp written by Lisp (desktop/tools/export-store-golden.lisp) load
/// and re-save byte-identically, and the config this client writes is the
/// committed config-csharp.sexp, which desktop/tools/verify-store-config.lisp
/// loads with the Lisp client's load-config! to check every value.
/// </summary>
public class StoreGoldenTests
{
    private static string GoldenPath(string name) => Path.Combine(AppContext.BaseDirectory, "golden", "store", name);

    [Fact]
    public void ALispWrittenQueueReSavesByteIdentically()
    {
        using var dir = new TempDir();
        var path = dir.File("queue.sexp");
        File.Copy(GoldenPath("queue-lisp.sexp"), path);
        var queue = new RunQueue(path);
        queue.Load();
        Assert.Equal(3, queue.Entries.Count);
        Assert.Equal(RunStatus.Queued, queue.Entries[0].Status);
        Assert.Equal("ロスト ヒート ソード", queue.Entries[0].QuestName);
        Assert.Equal(@"C:\Users\テスト\Videos\RappyRuns\run.mp4", queue.Entries[1].VideoPath);
        Assert.Equal(-1500, queue.Entries[2].Get(RunKeys.GhostDeltaMs).AsLong);

        File.Delete(path);
        queue.Save();
        Assert.Equal(File.ReadAllBytes(GoldenPath("queue-lisp.sexp")), File.ReadAllBytes(path));
    }

    [Fact]
    public void ALispWrittenConfigReSavesByteIdentically()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "config.sexp");
        File.Copy(GoldenPath("config-lisp.sexp"), path);
        var config = ConfigStore.Open(dir.Path);
        Assert.Equal(Language.Ja, config.Language);
        Assert.Equal("CUSTOM", config.OverlayCorner);
        Assert.Equal((0.7894558f, 0.020304568f), config.OverlayPosition);
        Assert.Equal(7.5, config.RecordMaxTotalGb);
        Assert.Equal(8053063680L, config.RecordMaxTotalBytes);
        Assert.Equal("D:\\録画\\\"rr\"", config.RecordDir);
        Assert.Equal("someone/fork", config.ResolveUpdateRepo());
        Assert.True(config.TrackingOnly);
        Assert.False(config.TrackingPrivate);
        Assert.True(config.Get("overlay-capturable").IsTrue);

        File.Delete(path);
        config.Save();
        Assert.Equal(File.ReadAllBytes(GoldenPath("config-lisp.sexp")), File.ReadAllBytes(path));
    }

    /// <summary>The config verify-store-config.lisp checks on the Lisp side; keep the two in sync.</summary>
    internal static ConfigStore BuildCSharpConfig(string directory)
    {
        var config = ConfigStore.Open(directory);
        config.ApiToken = "eta_tok\"en\\x";
        config.Language = Language.Ja;
        config.OverlayCorner = "CUSTOM";
        config.OverlayPosition = (0.25f, 0.7894558f);
        config.SetRecordMaxTotalGb(2.5);
        config.PinshareChannel = "パーティ";
        config.DebugSetting = true;
        config.AutoUpdate = false;
        config.RecordDir = @"D:\Videos\RR";
        config.ServerUrl = ConfigStore.TrimServerUrl("http://localhost:8080/ ");
        return config;
    }

    [Fact]
    public void TheCSharpWrittenConfigIsTheOneLispVerified()
    {
        using var dir = new TempDir();
        var config = BuildCSharpConfig(dir.Path);
        config.Save();
        Assert.Equal(File.ReadAllText(GoldenPath("config-csharp.sexp")), File.ReadAllText(config.FilePath));
    }
}

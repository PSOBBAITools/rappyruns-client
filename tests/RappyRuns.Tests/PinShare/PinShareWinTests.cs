using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RappyRuns.Core.PinShare;
using RappyRuns.Win.PinShare;

namespace RappyRuns.Tests.PinShare;

/// <summary>A throwaway game folder: PsoBB.exe, the Solybum plugin and a bundled data\pin-share.</summary>
internal sealed class GameFolder : IDisposable
{
    private readonly TempDir _dir = new("rr-pinshare");

    public GameFolder(bool plugin = true, bool dll = true)
    {
        Directory.CreateDirectory(Path.Combine(Root, "game", "addons"));
        Directory.CreateDirectory(Bundled);
        File.WriteAllText(Exe, "");
        if (plugin) File.WriteAllText(Path.Combine(Root, "game", "addons", "init.lua"), "-- plugin");
        File.WriteAllText(Path.Combine(Bundled, "init.lua"), "-- pin share addon v2");
        if (dll) File.WriteAllBytes(Path.Combine(Bundled, "pinshare-input.dll"), [1, 2, 3, 4]);
    }

    public string Root => _dir.Path;
    public string Exe => Path.Combine(Root, "game", "PsoBB.exe");
    public string Bundled => Path.Combine(Root, "client", "data", "pin-share");
    public string AddonDir => Path.Combine(Root, "game", "addons", "Pin Share");
    public string InPath => Path.Combine(AddonDir, "exchange", "in.txt");
    public string OutPath => Path.Combine(AddonDir, "exchange", "out.txt");

    public AddonInstaller Installer(Action<string>? log = null) => new([Bundled], log, () => 3900000000);

    public void Dispose() => _dir.Dispose();
}

public class PinShareInstallerTests
{
    [Fact(DisplayName = "install: no addon plugin means no-addon-plugin")]
    public void NoPlugin()
    {
        using var game = new GameFolder(plugin: false);
        Assert.Equal(PinShareStatusKind.NoAddonPlugin, game.Installer().EnsureAddon(game.AddonDir)?.Kind);
    }

    [Fact(DisplayName = "install: addon and dll copied, exchange created, options.lua untouched")]
    public void FreshInstall()
    {
        using var game = new GameFolder();
        Directory.CreateDirectory(game.AddonDir);
        File.WriteAllText(Path.Combine(game.AddonDir, "options.lua"), "return {cursorKey=117}");
        File.WriteAllText(Path.Combine(game.AddonDir, "init.lua"), "-- old addon");
        Assert.Null(game.Installer().EnsureAddon(game.AddonDir));
        Assert.Equal("-- pin share addon v2", File.ReadAllText(Path.Combine(game.AddonDir, "init.lua")));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(game.AddonDir, "pinshare-input.dll")));
        Assert.True(Directory.Exists(Path.Combine(game.AddonDir, "exchange")));
        Assert.Equal("return {cursorKey=117}", File.ReadAllText(Path.Combine(game.AddonDir, "options.lua")));
    }

    [Fact(DisplayName = "install: nothing bundled and nothing installed is install-failed")]
    public void NothingBundled()
    {
        using var game = new GameFolder();
        var status = new AddonInstaller([Path.Combine(game.Root, "nowhere")]).EnsureAddon(game.AddonDir);
        Assert.Equal(new PinShareStatus(PinShareStatusKind.InstallFailed, "init.lua is missing"), status);
    }

    [Fact(DisplayName = "install: a loaded (locked) dll moves aside and the new one is written")]
    public void LockedDllMovesAside()
    {
        using var game = new GameFolder();
        Directory.CreateDirectory(game.AddonDir);
        var installed = Path.Combine(game.AddonDir, "pinshare-input.dll");
        File.WriteAllBytes(installed, [9, 9]);
        // A loaded DLL: no writing, but renaming is allowed.
        using (new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            game.Installer().InstallInputDll(game.AddonDir);
        }
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(installed));
        var aside = Path.Combine(game.AddonDir, "pinshare-input.dll.old-3900000000");
        Assert.Equal([9, 9], File.ReadAllBytes(aside));
        // The next install cleans the aside copy once nothing holds it.
        game.Installer().InstallInputDll(game.AddonDir);
        Assert.False(File.Exists(aside));
    }

    [Fact(DisplayName = "install: a refused dll write puts the working copy back and logs the real reason (S39)")]
    public void RefusedDllWriteRestores()
    {
        using var game = new GameFolder();
        Directory.CreateDirectory(game.AddonDir);
        var installed = Path.Combine(game.AddonDir, "pinshare-input.dll");
        File.WriteAllBytes(installed, [9, 9]);
        var log = new List<string>();
        var refusing = new AddonInstaller([game.Bundled], log.Add, () => 3900000000, (path, bytes) =>
        {
            File.WriteAllBytes(path, bytes[..1]); // a partial file, then the refusal
            throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
        });
        refusing.InstallInputDll(game.AddonDir);
        Assert.Equal([9, 9], File.ReadAllBytes(installed)); // the working copy, whole
        Assert.Empty(AddonInstaller.OldInputDlls(game.AddonDir));
        Assert.Contains(log, line => line.StartsWith("pin share: input dll not installed: UnauthorizedAccessException: Access to the path", StringComparison.Ordinal));
        // A second failed attempt loses nothing either.
        refusing.InstallInputDll(game.AddonDir);
        Assert.Equal([9, 9], File.ReadAllBytes(installed));
        // Then a working install replaces it.
        game.Installer().InstallInputDll(game.AddonDir);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(installed));
    }

    [Fact(DisplayName = "install: a working copy left aside by a failed restore is brought back, not deleted (S39)")]
    public void AsideCopyRestoredWhenDllMissing()
    {
        using var game = new GameFolder();
        Directory.CreateDirectory(game.AddonDir);
        File.WriteAllBytes(Path.Combine(game.AddonDir, "pinshare-input.dll.old-3800000000"), [7]);
        File.WriteAllBytes(Path.Combine(game.AddonDir, "pinshare-input.dll.old-3800000100"), [8]);
        var log = new List<string>();
        new AddonInstaller([game.Bundled], log.Add, () => 3900000000, (_, _) => throw new IOException("disk full"))
            .InstallInputDll(game.AddonDir);
        var installed = Path.Combine(game.AddonDir, "pinshare-input.dll");
        Assert.Equal([8], File.ReadAllBytes(installed)); // the newest aside copy
        Assert.Contains("pin share: input dll restored from pinshare-input.dll.old-3800000100", log);
        Assert.Contains(log, line => line.EndsWith("IOException: disk full", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "install: a junction to a working copy is never written into; a dangling one is broken-link")]
    public void Junctions()
    {
        using var game = new GameFolder();
        var target = Path.Combine(game.Root, "working-copy");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "init.lua"), "-- dev addon");
        if (!MakeJunction(game.AddonDir, target)) return; // mklink unavailable: nothing to test
        Assert.True(AddonInstaller.IsReparsePoint(game.AddonDir));
        Assert.Null(game.Installer().EnsureAddon(game.AddonDir));
        Assert.Equal("-- dev addon", File.ReadAllText(Path.Combine(target, "init.lua")));
        Assert.False(File.Exists(Path.Combine(target, "pinshare-input.dll")));

        Directory.Delete(target, recursive: true);
        Assert.True(AddonInstaller.IsDanglingLink(game.AddonDir));
        Assert.Equal(new PinShareStatus(PinShareStatusKind.BrokenLink, game.AddonDir), game.Installer().EnsureAddon(game.AddonDir));
        Assert.True(Directory.Exists(game.AddonDir) || AddonInstaller.IsReparsePoint(game.AddonDir)); // the link is left alone
    }

    private static bool MakeJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        return process.ExitCode == 0;
    }
}

public class PinShareExchangeFileTests
{
    [Fact(DisplayName = "exchange: missing out.txt reads as empty; non-UTF-8 falls back to one char per byte")]
    public void ReadText()
    {
        using var game = new GameFolder();
        Assert.Equal("", ExchangeFiles.ReadText(Path.Combine(game.Root, "missing.txt")));
        var path = Path.Combine(game.Root, "latin.txt");
        File.WriteAllBytes(path, [(byte)'1', (byte)'\t', (byte)'n', 0xE9, (byte)'\n']);
        Assert.Equal("1\tn\u00e9\n", ExchangeFiles.ReadText(path));
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("1\t集合\n"));
        Assert.Equal("1\t集合\n", ExchangeFiles.ReadText(path));
    }

    [Fact(DisplayName = "exchange: in.txt is replaced whole, UTF-8 without BOM")]
    public void ReplaceInbox()
    {
        using var game = new GameFolder();
        var inPath = Path.Combine(game.Root, "in.txt");
        File.WriteAllText(inPath, "old");
        Assert.True(ExchangeFiles.ReplaceInbox(inPath, inPath + ".tmp", "session\tx\n集合\nend\n"));
        Assert.Equal(Encoding.UTF8.GetBytes("session\tx\n集合\nend\n"), File.ReadAllBytes(inPath));
        Assert.False(File.Exists(inPath + ".tmp"));
    }
}

public class PinShareRelayIntegrationTests
{
    private static async Task WaitFor(Func<bool> condition, string what, int seconds = 15)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException($"timed out waiting for {what}");
            await Task.Delay(20);
        }
    }

    private static string Inbox(GameFolder game) => ExchangeFiles.ReadText(game.InPath);

    private static bool HasMessage(FakeRelayServer server, Func<JsonElement, bool> match) =>
        server.Received.Any(m => PinShareJson.TryParse(m) is { } e && match(e));

    private static string? T(JsonElement e, string key) => PinShareJson.AsString(JsonItem.From(e)?.Get(key));

    [Fact(DisplayName = "relay: connects, relays out.txt to the server and the server's state to in.txt, reconnects, cleans up")]
    public async Task EndToEnd()
    {
        using var game = new GameFolder();
        using var server = new FakeRelayServer();
        var config = new PinShareConfig(true, " secret ", server.Url);
        using var supervisor = new PinShareSupervisor(() => config, new PinSharePermission(true), installer: game.Installer());
        supervisor.GameExe = game.Exe;
        // A stale command from before the session: identity kept, pin skipped.
        Directory.CreateDirectory(Path.GetDirectoryName(game.OutPath)!);
        File.WriteAllText(game.OutPath, "50\tname\tTeapot\n51\tadd\t1\t1.000\t2.000\t3.000\t60\n");
        supervisor.Start();

        await WaitFor(() => Inbox(game).Contains("status\tconnected\t\n", StringComparison.Ordinal), "connected in.txt");
        Assert.Equal("-- pin share addon v2", File.ReadAllText(Path.Combine(game.AddonDir, "init.lua")));
        await WaitFor(() => HasMessage(server, e => T(e, "t") == "hello" && T(e, "channel") == "secret" && T(e, "name") == "Teapot"), "hello");
        Assert.False(HasMessage(server, e => T(e, "t") == "add"));
        Assert.Equal(PinShareStatusKind.ConnectedNoAddon, supervisor.Status.Kind);
        Assert.Contains("ack\t51\n", Inbox(game), StringComparison.Ordinal);

        // The addon speaks: a live add goes out, and the Settings line flips.
        File.AppendAllText(game.OutPath, "52\tadd\t10203\t1.500\t0.000\t-2.250\t60\tgo\t3\t7\n");
        await WaitFor(() => HasMessage(server, e => T(e, "t") == "add" && T(e, "label") == "go"), "add");
        await WaitFor(() => supervisor.Status.Kind == PinShareStatusKind.Connected, "connected status");

        await server.BroadcastAsync("{\"t\":\"state\",\"pins\":[{\"id\":3,\"owner\":\"Teapot\",\"floor\":10203,\"x\":1.5,\"y\":0,\"z\":-2.25,\"label\":\"go\",\"no\":1,\"ownerNo\":1,\"room\":7,\"roomNo\":1,\"color\":\"ff8c00\",\"remaining\":60}],\"arrows\":[],\"members\":[\"Teapot\",\"Kettle\"]}")
            ;
        await WaitFor(() => Inbox(game).Contains("pin\t3\tTeapot\t10203\t1.5\t0\t-2.25\t60\tgo\t1\t1\tff8c00\t1\t7\n", StringComparison.Ordinal), "pin in in.txt");
        await WaitFor(() => supervisor.Status == new PinShareStatus(PinShareStatusKind.Connected, "secret", 2), "member count");
        Assert.Single(supervisor.ChannelItems!.Pins);

        // A server error shows in Settings; in.txt stays connected.
        await server.BroadcastAsync("{\"t\":\"error\",\"message\":\"rate limited\"}");
        await WaitFor(() => supervisor.Status == new PinShareStatus(PinShareStatusKind.Error, "server: rate limited"), "server error");

        // Dropped: lists cleared, then a reconnect re-introduces the player.
        var connections = server.Connections;
        server.DropAll();
        await WaitFor(() => Inbox(game).Contains("status\terror\tdisconnected from the server\n", StringComparison.Ordinal)
                      || server.Connections > connections, "disconnect noticed");
        await WaitFor(() => server.Connections > connections, "reconnect", 20);
        await WaitFor(() => Inbox(game).Contains("status\tconnected\t\n", StringComparison.Ordinal), "connected again");

        // Turning Pin Share off ends the session and empties in.txt.
        config = config with { Enabled = false };
        await WaitFor(() => supervisor.Status.Kind == PinShareStatusKind.Off, "off");
        Assert.DoesNotContain("pin\t", Inbox(game), StringComparison.Ordinal);
        Assert.Null(supervisor.ChannelItems);
        supervisor.Stop();
    }

    [Fact(DisplayName = "relay: no passphrase but a pin set draws it locally and never connects")]
    public async Task LocalOnly()
    {
        using var game = new GameFolder();
        var tracker = new PinSetTracker(_ => ["ep1-q"], () => true);
        var connects = 0;
        using var supervisor = new PinShareSupervisor(
            () => new PinShareConfig(true, "", null), new PinSharePermission(true), tracker, game.Installer(),
            connect: (_, _) => { Interlocked.Increment(ref connects); throw new InvalidOperationException("no server"); });
        supervisor.GameExe = game.Exe;
        supervisor.Start();
        await WaitFor(() => supervisor.Status.Kind == PinShareStatusKind.NoChannel, "no-channel");

        tracker.FetchWanted(new PinShareQuest(42, "Q"));
        tracker.Land(tracker.LoadId, new PinSet(PinShareJson.TryParse("""{"name":"Route","items":{"pins":[{"area":1,"x":0,"y":0,"z":0}]}}""")!.Value));
        await WaitFor(() => Inbox(game).Contains("pinset\tRoute\nend\n", StringComparison.Ordinal), "local set drawn");
        Assert.Contains("status\tlocal\t\n", Inbox(game), StringComparison.Ordinal);
        Assert.Equal(new PinShareStatus(PinShareStatusKind.LocalOnly, "Route"), supervisor.Status);

        // Leaving the quest drops the set: the session ends and in.txt loses it.
        tracker.FetchWanted(new PinShareQuest(0, null));
        await WaitFor(() => supervisor.Status.Kind == PinShareStatusKind.NoChannel, "no-channel again");
        Assert.DoesNotContain("pinset", Inbox(game), StringComparison.Ordinal);
        Assert.Equal(0, connects);
    }

    [Fact(DisplayName = "relay: a fresh heartbeat from another relay is a conflict until it goes stale")]
    public async Task Conflict()
    {
        using var game = new GameFolder();
        Directory.CreateDirectory(Path.GetDirectoryName(game.InPath)!);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        File.WriteAllText(game.InPath, $"session\tfeedbeef\ntime\t{now}\nend\n");
        using var supervisor = new PinShareSupervisor(
            () => new PinShareConfig(true, "secret", "ws://127.0.0.1:1/"), new PinSharePermission(true), installer: game.Installer());
        supervisor.GameExe = game.Exe;
        supervisor.Start();
        await WaitFor(() => supervisor.Status.Kind == PinShareStatusKind.Conflict, "conflict");
        File.WriteAllText(game.InPath, "session\tfeedbeef\ntime\t1000\nend\n");
        await WaitFor(() => supervisor.Status.Kind is PinShareStatusKind.Connecting or PinShareStatusKind.Error, "stood down");
    }

    [Fact(DisplayName = "relay: the gate order is off, not-allowed, no-channel, waiting-game")]
    public void WantedOrder()
    {
        var config = new PinShareConfig(false, "", null);
        var permission = new PinSharePermission(false);
        using var supervisor = new PinShareSupervisor(() => config, permission);
        Assert.Equal(PinShareStatusKind.Off, supervisor.Wanted().Status.Kind);
        config = config with { Enabled = true };
        Assert.Equal(PinShareStatusKind.NotAllowed, supervisor.Wanted().Status.Kind);
        permission.Set(true);
        Assert.Equal(PinShareStatusKind.NoChannel, supervisor.Wanted().Status.Kind);
        config = config with { Channel = "secret" };
        Assert.Equal(PinShareStatusKind.WaitingGame, supervisor.Wanted().Status.Kind);
        supervisor.GameExe = @"C:\Games\PsoBB.exe";
        Assert.Equal(new PinShareWanted(@"C:\Games\PsoBB.exe", "secret", PinShareSettings.DefaultServer), supervisor.Wanted().Wanted);
    }
}

public class PinSharePermissionAndFetchTests
{
    private static JsonElement Json(string text) => PinShareJson.TryParse(text)!.Value;

    [Fact(DisplayName = "permission: /api/me decides, transport errors and stale answers keep the verdict")]
    public async Task Refresh()
    {
        var persisted = new List<bool>();
        var permission = new PinSharePermission(false, persisted.Add);
        var token = "tok";
        await permission.RefreshOnceAsync(() => token, (_, _) => Task.FromResult(new MeResult(MeOutcome.Ok, Json("""{"features":["pinshare"]}"""))), default);
        Assert.True(permission.Allowed);
        await permission.RefreshOnceAsync(() => token, (_, _) => throw new HttpRequestException("offline"), default);
        Assert.True(permission.Allowed);
        await permission.RefreshOnceAsync(() => token, (_, _) => { token = "other"; return Task.FromResult(new MeResult(MeOutcome.Unauthorized, null)); }, default);
        Assert.True(permission.Allowed);
        await permission.RefreshOnceAsync(() => token, (_, _) => Task.FromResult(new MeResult(MeOutcome.Unauthorized, null)), default);
        Assert.False(permission.Allowed);
        token = "";
        await permission.RefreshOnceAsync(() => token, (_, _) => throw new InvalidOperationException("not called"), default);
        Assert.Equal([true, false], persisted);
    }

    [Fact(DisplayName = "pin sets: fetched once per quest load, a late answer for an old load is dropped")]
    public async Task FetchOncePerLoad()
    {
        var fetches = new List<(string, IReadOnlyList<string>)>();
        var allowed = true;
        var tracker = new PinSetTracker(
            q => q.QuestName == "none" ? [] : ["ep1-a", "ep1-b"],
            () => allowed,
            (slug, extra, _) =>
            {
                fetches.Add((slug, extra));
                return Task.FromResult<JsonElement?>(Json("""{"id":3,"name":"S","author":"T","mine":1,"items":{"pins":[]}}"""));
            });
        await tracker.OnSnapshot(new PinShareQuest(10, "A"))!;
        Assert.Equal("S", tracker.Current!.DisplayName);
        Assert.True(tracker.Current.Mine);
        Assert.Equal(3, tracker.Current.Id);
        Assert.Equal([("ep1-a", (IReadOnlyList<string>)["ep1-b"])], fetches.Select(f => (f.Item1, f.Item2)).ToList(), new FetchComparer());
        Assert.Null(tracker.OnSnapshot(new PinShareQuest(10, "A")));
        Assert.Equal("Pin set: \"S\" by T", tracker.PinSetText(RappyRuns.Core.I18n.Language.En));

        // A late answer for load 10 after load 11 started is dropped.
        var load10 = tracker.LoadId;
        tracker.FetchWanted(new PinShareQuest(11, "B"));
        Assert.False(tracker.Land(load10, new PinSet(Json("""{"name":"old","items":{}}"""))));
        Assert.Null(tracker.Current);

        // Refetch after an overwrite asks again for the same load.
        tracker.Refetch();
        Assert.NotNull(tracker.OnSnapshot(new PinShareQuest(11, "B")));

        // Not allowed: the slugs are still recorded for Save, nothing fetched.
        allowed = false;
        Assert.Null(tracker.OnSnapshot(new PinShareQuest(12, "C")));
        Assert.Equal(["ep1-a", "ep1-b"], tracker.QuestSlugs!);
        Assert.Null(tracker.OnSnapshot(new PinShareQuest(13, "none")));
        Assert.Null(tracker.QuestSlugs);
        Assert.Equal(
            "Pin set: none for this quest (choose one on the quest's Pin sets tab on the site)",
            new PinSetTracker(_ => ["x"], () => false).Also(t => t.FetchWanted(new PinShareQuest(1, "Q"))).PinSetText(RappyRuns.Core.I18n.Language.En));
    }

    [Fact(DisplayName = "pin sets: a late reply for an earlier load at the same address, or from before a Refetch or Reset, is dropped (S36)")]
    public async Task LateReplySameAddress()
    {
        // The fetch runs on a pool thread: each call hands the test its reply to complete.
        using var replies = new System.Collections.Concurrent.BlockingCollection<TaskCompletionSource<JsonElement?>>();
        TaskCompletionSource<JsonElement?> Next() =>
            replies.TryTake(out var reply, TimeSpan.FromSeconds(10)) ? reply : throw new TimeoutException("no fetch started");
        var tracker = new PinSetTracker(_ => ["ep1-a"], () => true, (_, _, _) =>
        {
            var reply = new TaskCompletionSource<JsonElement?>();
            replies.Add(reply);
            return reply.Task;
        });
        JsonElement Set(string name) => Json($$$"""{"name":"{{{name}}}","items":{"pins":[]}}""");

        var old = tracker.OnSnapshot(new PinShareQuest(10, "A"))!;
        var first = Next();
        tracker.FetchWanted(new PinShareQuest(0, null)); // back to the lobby
        var fresh = tracker.OnSnapshot(new PinShareQuest(10, "B"))!; // next quest, same address
        first.SetResult(Set("old"));
        await old;
        Assert.Null(tracker.Current);
        Next().SetResult(Set("new"));
        await fresh;
        Assert.Equal("new", tracker.Current!.DisplayName);

        // Refetch (after an overwrite) starts a fresh fetch for the same load.
        tracker.Refetch();
        var again = tracker.OnSnapshot(new PinShareQuest(10, "B"))!;
        Assert.Equal(["ep1-a"], tracker.QuestSlugs!); // the same quest: its slugs never blink out
        var pending = Next();

        // The game exits and a relaunch loads a quest at the same address.
        tracker.Reset();
        Assert.Null(tracker.OnSnapshot(null));
        var relaunched = tracker.OnSnapshot(new PinShareQuest(10, "C"))!;
        var relaunchReply = Next();
        pending.SetResult(Set("pre-exit"));
        await again;
        Assert.Null(tracker.Current);
        relaunchReply.SetResult(Set("relaunched"));
        await relaunched;
        Assert.Equal("relaunched", tracker.Current!.DisplayName);
    }

    [Fact(DisplayName = "pin sets: another quest at the same address with no lobby frame between is a new load (S36)")]
    public void SameAddressNewName()
    {
        var tracker = new PinSetTracker(q => [q.QuestName == "A" ? "ep1-a" : "ep1-b"], () => true);
        Assert.NotNull(tracker.FetchWanted(new PinShareQuest(10, "A")));
        var loadA = tracker.LoadId;
        Assert.Equal(["ep1-b"], tracker.FetchWanted(new PinShareQuest(10, "B"))!);
        Assert.False(tracker.Land(loadA, null));
        Assert.Equal(["ep1-b"], tracker.QuestSlugs!);
    }

    [Fact(DisplayName = "pin sets: a fetch payload without an items object is no set")]
    public void FetchPayloadShape()
    {
        Assert.Null(PinSet.FromFetch(null));
        Assert.Null(PinSet.FromFetch(Json("""{"name":"x"}""")));
        Assert.Null(PinSet.FromFetch(Json("""{"items":[]}""")));
        Assert.NotNull(PinSet.FromFetch(Json("""{"items":{}}""")));
    }

    [Fact(DisplayName = "save: prechecks and reports")]
    public void SaveTexts()
    {
        var items = new ChannelItems([Json("{}")], []);
        Assert.Equal(PinSetSaveBlock.NoQuest, PinSetSave.Precheck(null, items));
        Assert.Equal(PinSetSaveBlock.NoItems, PinSetSave.Precheck(["q"], new ChannelItems([], [])));
        Assert.Equal(PinSetSaveBlock.None, PinSetSave.Precheck(["q"], items));
        Assert.Equal(PinSetSaveBlock.NotMine, PinSetSave.Precheck(["q"], items, overwrite: true, inUse: new PinSet(Json("""{"mine":0}"""))));
        Assert.Equal(PinSetSaveBlock.None, PinSetSave.Precheck(["q"], items, overwrite: true, inUse: new PinSet(Json("""{"mine":1}"""))));
        var en = RappyRuns.Core.I18n.Language.En;
        Assert.Equal("Pin set updated (4 pins, 0 arrows).", PinSetSave.ReportText(en, PinSetSaveOutcome.Updated, Json("""{"pins":4}""")));
        Assert.Equal("Could not save the pin set: too many", PinSetSave.ReportText(en, PinSetSaveOutcome.Rejected, Json("""{"error":"too many"}""")));
        Assert.Equal("Could not save the pin set: not-found", PinSetSave.ReportText(en, PinSetSaveOutcome.NotFound, null));
        Assert.Contains("https://x/pin-sets/1", PinSetSave.ReportText(en, PinSetSaveOutcome.Created, Json("""{"url":"https://x/pin-sets/1"}""")), StringComparison.Ordinal);
    }

    private sealed class FetchComparer : IEqualityComparer<(string, IReadOnlyList<string>)>
    {
        public bool Equals((string, IReadOnlyList<string>) x, (string, IReadOnlyList<string>) y) => x.Item1 == y.Item1 && x.Item2.SequenceEqual(y.Item2);

        public int GetHashCode((string, IReadOnlyList<string>) obj) => obj.Item1.GetHashCode(StringComparison.Ordinal);
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}

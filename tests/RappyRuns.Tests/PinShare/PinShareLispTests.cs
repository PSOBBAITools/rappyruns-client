using System.Text.Json;
using RappyRuns.Core.I18n;
using RappyRuns.Core.PinShare;

namespace RappyRuns.Tests.PinShare;

/// <summary>
/// Every check of client/tests/tests-pinshare.lisp, in order, under its
/// Lisp label. The file formats are a contract with the Lua addon
/// (data/pin-share/init.lua readInbox / sendCommand) and the JSON with the
/// relay server, so the expectations are spelled out literally.
/// </summary>
public class PinShareLispTests
{
    private static string TabLine(params object[] fields) =>
        string.Join('\t', fields.Select(f => Convert.ToString(f, System.Globalization.CultureInfo.InvariantCulture))) + "\n";

    private static JsonElement Parsed(string json) => PinShareJson.TryParse(json) ?? throw new InvalidDataException(json);

    private static JsonElement? Get(JsonElement o, string key) => JsonItem.From(o)!.Get(key);

    private static List<OutboxLine> Lines(params string[][] lines) =>
        [.. lines.Select(fields => new OutboxLine(long.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture), fields))];

    // --- out.txt parsing ---------------------------------------------------

    [Fact(DisplayName = "complete out.txt lines parse into (seq . fields)")]
    public void CompleteLines()
    {
        var lines = Outbox.Parse(TabLine(10, "name", "Teapot") + TabLine(11, "clear_mine"));
        Assert.Equal(2, lines.Count);
        Assert.Equal(10, lines[0].Seq);
        Assert.Equal(["10", "name", "Teapot"], lines[0].Fields);
        Assert.Equal(11, lines[1].Seq);
        Assert.Equal(["11", "clear_mine"], lines[1].Fields);
    }

    [Fact(DisplayName = "a line the addon is still writing is left for the next read")]
    public void PartialLine()
    {
        var lines = Outbox.Parse(TabLine(10, "clear_mine") + "11\tadd\t1");
        Assert.Single(lines);
        Assert.Equal(["10", "clear_mine"], lines[0].Fields);
    }

    [Fact(DisplayName = "out.txt without any newline yields nothing")]
    public void NoNewline() => Assert.Empty(Outbox.Parse("10\tclear_mine"));

    [Fact(DisplayName = "CRLF line ends and junk lines are tolerated")]
    public void CrlfAndJunk()
    {
        var lines = Outbox.Parse("garbage\n12\tremove\t5\r\n\n");
        Assert.Single(lines);
        Assert.Equal(12, lines[0].Seq);
        Assert.Equal(["12", "remove", "5"], lines[0].Fields);
    }

    // --- number parsing ----------------------------------------------------

    [Fact(DisplayName = "the addon's %.3f coordinates parse exactly")]
    public void CoordinatesParse()
    {
        Assert.Equal(-123.456d, PinShareText.ParseNumber("-123.456"));
        Assert.Equal(0d, PinShareText.ParseNumber("0.000"));
        Assert.Equal(1000d, PinShareText.ParseNumber("1e3"));
    }

    [Fact(DisplayName = "nan, inf, empty and trailing junk are refused")]
    public void JunkRefused()
    {
        foreach (var text in new[] { "nan", "inf", "-nan(ind)", "", "-", ".", "1.5x", "1e", "#.(quit)" })
            Assert.Null(PinShareText.ParseNumber(text));
    }

    [Fact(DisplayName = "integers accept an integral decimal only")]
    public void IntegersIntegralOnly()
    {
        Assert.Equal(60, PinShareText.ParseInteger("60"));
        Assert.Equal(60, PinShareText.ParseInteger("60.000"));
        Assert.Null(PinShareText.ParseInteger("60.5"));
    }

    // --- command translation -----------------------------------------------

    [Fact(DisplayName = "add carries floor, point, ttl, label, max and room")]
    public void AddCarriesAll()
    {
        var m = Parsed(Commands.Message(["1", "add", "10203", "1.500", "0.000", "-2.250", "60", "集合", "3", "7"])!);
        Assert.Equal("add", Get(m, "t")!.Value.GetString());
        Assert.Equal(10203, Get(m, "floor")!.Value.GetInt64());
        Assert.Equal(1.5, Get(m, "x")!.Value.GetDouble());
        Assert.Equal(-2.25, Get(m, "z")!.Value.GetDouble());
        Assert.Equal(60, Get(m, "ttl")!.Value.GetInt64());
        Assert.Equal("集合", Get(m, "label")!.Value.GetString());
        Assert.Equal(3, Get(m, "max")!.Value.GetInt64());
        Assert.Equal(7, Get(m, "room")!.Value.GetInt64());
    }

    [Fact(DisplayName = "an old addon's short add omits max and room")]
    public void ShortAdd()
    {
        var m = Parsed(Commands.Message(["1", "add", "1", "1.000", "2.000", "3.000", "0"])!);
        Assert.Equal("", Get(m, "label")!.Value.GetString());
        Assert.Null(Get(m, "max"));
        Assert.Null(Get(m, "room"));
    }

    [Fact(DisplayName = "an add with a broken coordinate is dropped")]
    public void BrokenAdd() => Assert.Null(Commands.Message(["1", "add", "1", "nan", "2.000", "3.000", "0", "", "3"]));

    [Fact(DisplayName = "arrow_add carries both ends, the bend point and the room")]
    public void ArrowAdd()
    {
        var m = Parsed(Commands.Message(["1", "arrow_add", "5", "1.000", "2.000", "3.000", "4.000", "5.000", "6.000", "30", "3", "2.500", "3.500", "9.000", "12"])!);
        Assert.Equal("arrow_add", Get(m, "t")!.Value.GetString());
        Assert.Equal(1d, Get(m, "x1")!.Value.GetDouble());
        Assert.Equal(6d, Get(m, "z2")!.Value.GetDouble());
        Assert.Equal(9d, Get(m, "zm")!.Value.GetDouble());
        Assert.Equal(30, Get(m, "ttl")!.Value.GetInt64());
        Assert.Equal(3, Get(m, "max")!.Value.GetInt64());
        Assert.Equal(12, Get(m, "room")!.Value.GetInt64());
    }

    [Fact(DisplayName = "arrow_move without a bend point sends the ends only")]
    public void ArrowMoveNoBend()
    {
        var m = Parsed(Commands.Message(["1", "arrow_move", "8", "1.000", "2.000", "3.000", "4.000", "5.000", "6.000"])!);
        Assert.Equal(8, Get(m, "id")!.Value.GetInt64());
        Assert.Null(Get(m, "xm"));
    }

    [Fact(DisplayName = "remove / arrow_remove / clear_* translate")]
    public void RemoveAndClear()
    {
        Assert.Equal(4, Get(Parsed(Commands.Message(["1", "arrow_remove", "4"])!), "id")!.Value.GetInt64());
        Assert.Equal("clear_all", Get(Parsed(Commands.Message(["1", "clear_all"])!), "t")!.Value.GetString());
    }

    [Fact(DisplayName = "an unknown command is ignored")]
    public void UnknownIgnored() => Assert.Null(Commands.Message(["1", "teleport", "1"]));

    // --- relay state machine -----------------------------------------------

    private static PinShareRelay BacklogRelay()
    {
        var relay = new PinShareRelay { Channel = "secret" };
        relay.SkipBacklog(Lines(["5", "name", "Teapot"], ["6", "color", "FF8C00"], ["7", "add", "1", "1.000", "2.000", "3.000", "60", "", "3"]));
        return relay;
    }

    private const string StateMessage =
        "{\"t\":\"state\",\"pins\":[{\"id\":3,\"owner\":\"Tea\\tpot\",\"floor\":10203,\"x\":1.5,\"y\":0,\"z\":-2.25,\"label\":\"集合\",\"no\":4,\"ownerNo\":2,\"room\":7,\"roomNo\":1,\"color\":\"ff8c00\",\"remaining\":42},{\"id\":4,\"owner\":\"Old\",\"floor\":1,\"x\":1,\"y\":2,\"z\":3,\"label\":\"\",\"no\":5,\"ownerNo\":1,\"room\":null,\"roomNo\":null,\"color\":\"\",\"remaining\":-1},{\"id\":\"bad\",\"floor\":1,\"x\":1,\"y\":2,\"z\":3}],\"arrows\":[{\"id\":9,\"owner\":\"Kettle\",\"floor\":5,\"room\":12,\"x1\":1,\"y1\":2,\"z1\":3,\"x2\":4,\"y2\":5,\"z2\":6,\"xm\":2.5,\"ym\":3.5,\"zm\":9,\"color\":\"66e0ff\",\"remaining\":30}],\"members\":[\"Teapot\",\"Kettle\"]}";

    // The Lisp test threads one relay through the checks below; each C#
    // test replays the prefix it depends on.
    private static PinShareRelay AfterRename()
    {
        var relay = BacklogRelay();
        relay.Consume(Lines(["7", "clear_all"]), true);
        relay.Consume(Lines(["8", "clear_all"]), false);
        relay.Consume(Lines(["8", "clear_all"]), true);
        relay.Consume(Lines(["9", "arrow_color", ""], ["10", "name", "Kettle"]), true);
        relay.Session = "abcd1234";
        relay.SetStatus("connected", "");
        relay.NoteMessage(StateMessage);
        return relay;
    }

    [Fact(DisplayName = "the start-up backlog keeps identity and skips stale pins")]
    public void BacklogKeepsIdentity()
    {
        var relay = BacklogRelay();
        Assert.Equal(7, relay.LastSeq);
        Assert.Equal("Teapot", relay.Name);
        Assert.Equal("FF8C00", relay.Color);
    }

    [Fact(DisplayName = "the backlog is not proof the addon is alive now")]
    public void BacklogNotProof() => Assert.False(BacklogRelay().AddonSeen);

    [Fact(DisplayName = "hello introduces channel and name, then the pin color")]
    public void Hello()
    {
        var hello = BacklogRelay().HelloMessages().Select(Parsed).ToList();
        Assert.Equal(2, hello.Count);
        Assert.Equal("secret", Get(hello[0], "channel")!.Value.GetString());
        Assert.Equal("Teapot", Get(hello[0], "name")!.Value.GetString());
        Assert.Equal("FF8C00", Get(hello[1], "color")!.Value.GetString());
    }

    [Fact(DisplayName = "already-handled seqs are not replayed (and prove nothing)")]
    public void HandledSeqsNotReplayed()
    {
        var relay = BacklogRelay();
        Assert.Empty(relay.Consume(Lines(["7", "clear_all"]), true));
        Assert.False(relay.AddonSeen);
    }

    [Fact(DisplayName = "commands arriving while disconnected are dropped for good")]
    public void DisconnectedDropped()
    {
        var relay = BacklogRelay();
        relay.Consume(Lines(["7", "clear_all"]), true);
        Assert.Empty(relay.Consume(Lines(["8", "clear_all"]), false));
        // ...but a new command is the addon speaking to us
        Assert.True(relay.AddonSeen);
        Assert.Empty(relay.Consume(Lines(["8", "clear_all"]), true));
    }

    [Fact(DisplayName = "an empty arrow color is sent; a rename re-sends hello + colors")]
    public void ArrowColorAndRename()
    {
        var relay = BacklogRelay();
        relay.Consume(Lines(["7", "clear_all"]), true);
        relay.Consume(Lines(["8", "clear_all"]), false);
        relay.Consume(Lines(["8", "clear_all"]), true);
        var messages = relay.Consume(Lines(["9", "arrow_color", ""], ["10", "name", "Kettle"]), true);
        Assert.Equal(4, messages.Count);
        Assert.Equal("arrow_color", Get(Parsed(messages[0]), "t")!.Value.GetString());
        Assert.Equal("Kettle", Get(Parsed(messages[1]), "name")!.Value.GetString());
        Assert.Equal("arrow_color", Get(Parsed(messages[3]), "t")!.Value.GetString());
    }

    // --- server -> in.txt --------------------------------------------------

    [Fact(DisplayName = "in.txt renders exactly what the addon's readInbox expects")]
    public void InboxExact()
    {
        var expected =
            TabLine("session", "abcd1234")
            + TabLine("time", 1700000000)
            + TabLine("ack", 10)
            + TabLine("status", "connected", "")
            + TabLine("channel", "secret")
            + TabLine("member", "Teapot")
            + TabLine("member", "Kettle")
            + TabLine("pin", 3, "Teapot", 10203, "1.5", "0", "-2.25", 42, "集合", 4, 2, "ff8c00", 1, 7)
            + TabLine("pin", 4, "Old", 1, "1", "2", "3", -1, "", 5, 1, "", "", "")
            + TabLine("arrow", 9, "Kettle", 5, "1", "2", "3", "4", "5", "6", 30, "66e0ff", "2.5", "3.5", "9", 12)
            + TabLine("end");
        Assert.Equal(expected, Inbox.Render(AfterRename(), 1700000000));
    }

    [Fact(DisplayName = "the rendered text ends the way readInbox's terminator check wants")]
    public void InboxTerminator() => Assert.EndsWith("\nend\n", Inbox.Render(AfterRename(), 1), StringComparison.Ordinal);

    [Fact(DisplayName = "a server error message is returned for the log, state untouched")]
    public void ServerError()
    {
        var relay = AfterRename();
        Assert.Equal("rate limited", relay.NoteMessage("{\"t\":\"error\",\"message\":\"rate limited\"}"));
        Assert.Equal(3, relay.Pins.Count);
    }

    [Fact(DisplayName = "garbage from the server is ignored")]
    public void GarbageIgnored() => Assert.Null(AfterRename().NoteMessage("not json"));

    [Fact(DisplayName = "a disconnect empties the lists the addon draws from")]
    public void DisconnectEmpties()
    {
        var relay = AfterRename();
        relay.Dirty = false;
        relay.ClearState();
        Assert.Empty(relay.Pins);
        Assert.True(relay.Dirty);
    }

    // --- number formatting for Lua's tonumber ------------------------------

    [Fact(DisplayName = "numbers never carry a Lisp float marker")]
    public void NoFloatMarker()
    {
        Assert.Equal("12", PinShareText.FormatNumber(12L));
        Assert.Equal("-2.25", PinShareText.FormatNumber(-2.25d));
        Assert.Equal("1.5", PinShareText.FormatNumber((double)1.5f));
        Assert.DoesNotContain(PinShareText.FormatNumber(1.0e-7).Replace("e", "", StringComparison.Ordinal), char.IsLetter);
    }

    // --- the old PowerShell relay ------------------------------------------

    private static readonly string ForeignInbox = TabLine("session", "feedbeef") + TabLine("time", 1000) + TabLine("end");

    [Fact(DisplayName = "a fresh heartbeat from another relay is a conflict")]
    public void ForeignConflict() => Assert.True(Inbox.IsForeignRelay(ForeignInbox, ["abcd1234"], 1002));

    [Fact(DisplayName = "our own earlier session is not")]
    public void OwnSession() => Assert.False(Inbox.IsForeignRelay(ForeignInbox, ["abcd1234", "feedbeef"], 1002));

    [Fact(DisplayName = "a stale heartbeat is not")]
    public void StaleHeartbeat() => Assert.False(Inbox.IsForeignRelay(ForeignInbox, ["abcd1234"], 1010));

    [Fact(DisplayName = "an empty in.txt is not")]
    public void EmptyInbox() => Assert.False(Inbox.IsForeignRelay("", [], 1002));

    // --- settings ----------------------------------------------------------

    [Fact(DisplayName = "the passphrase is cleaned and trimmed like the server does")]
    public void ChannelCleaned() => Assert.Equal("secret", PinShareSettings.Channel("  se\tcret  "));

    [Fact(DisplayName = "a blank server setting means the public relay")]
    public void BlankServer()
    {
        Assert.Equal(PinShareSettings.DefaultServer, PinShareSettings.ServerUrl(null));
        Assert.Equal(PinShareSettings.DefaultServer, PinShareSettings.ServerUrl(""));
        Assert.Equal("wss://pin-share-server-production.up.railway.app", PinShareSettings.DefaultServer);
    }

    [Fact(DisplayName = "the addon folder sits next to the game exe")]
    public void AddonFolder() =>
        Assert.Equal(@"C:\Games\EphineaPSO\addons\Pin Share", PinShareSettings.AddonDir("C:/Games/EphineaPSO/PsoBB.exe"));

    [Fact(DisplayName = "ws / wss URLs get the right default ports")]
    public void WebSocketPorts()
    {
        Assert.Equal(new WebSocketUrl(true, "relay.example", 443, "/ws"), PinShareSettings.ParseWebSocketUrl("wss://relay.example/ws"));
        Assert.Equal(new WebSocketUrl(false, "localhost", 8787, "/"), PinShareSettings.ParseWebSocketUrl("ws://localhost:8787"));
    }

    // --- pin sets ----------------------------------------------------------

    private const string TtfSet =
        """
        {"id":7,"name":"TTF route","mine":1,
          "items":{"pins":[
            {"area":10101,"room":3,"x":1,"y":2.5,"z":-3,
             "label":"start","color":"ff8c00"},
            {"area":10101,"room":3,"x":4,"y":0,"z":0},
            {"area":10102,"x":5,"y":0,"z":0}],
          "arrows":[
            {"area":10101,"room":4,"x1":0,"y1":0,"z1":0,
             "x2":10,"y2":0,"z2":10,"xm":5,"ym":0,"zm":6}]}}
        """;

    private static (PinSet Set, PinShareRelay Relay, string Text) SetRender()
    {
        var set = new PinSet(Parsed(TtfSet));
        var relay = new PinShareRelay { Channel = "secret", Session = "abc" };
        return (set, relay, Inbox.Render(relay, 1, set));
    }

    [Fact(DisplayName = "pin-set pins join in.txt locked, negative ids, set name as owner")]
    public void SetPins()
    {
        var text = SetRender().Text;
        Assert.Contains(TabLine("pin", -1, "TTF route", 10101, 1, "2.5", -3, -1, "start", "", 1, "ff8c00", "", 3, 1), text, StringComparison.Ordinal);
        Assert.Contains(TabLine("pin", -2, "TTF route", 10101, 4, 0, 0, -1, "", "", 2, "", "", 3, 1), text, StringComparison.Ordinal);
        Assert.Contains(TabLine("pin", -3, "TTF route", 10102, 5, 0, 0, -1, "", "", 3, "", "", "", 1), text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "set pins carry only the per-owner number (no clash with the channel's)")]
    public void SetPinsOwnerNumberOnly() => Assert.DoesNotContain("start\t1\t", SetRender().Text, StringComparison.Ordinal);

    [Fact(DisplayName = "the set's local items are built once per set, not per render")]
    public void LocalItemsCached()
    {
        var set = SetRender().Set;
        Assert.Same(set.LocalItems.Pins, set.LocalItems.Pins);
        Assert.Same(set.LocalItems.Arrows, set.LocalItems.Arrows);
    }

    [Fact(DisplayName = "pin-set arrows keep their bend, room and the locked flag last")]
    public void SetArrows() =>
        Assert.Contains(TabLine("arrow", -4, "TTF route", 10101, 0, 0, 0, 10, 0, 10, -1, "", 5, 0, 6, 4, 1), SetRender().Text, StringComparison.Ordinal);

    [Fact(DisplayName = "the addon is told which set it is drawing, before end")]
    public void PinsetLine() =>
        Assert.Contains(TabLine("pinset", "TTF route") + TabLine("end"), SetRender().Text, StringComparison.Ordinal);

    [Fact(DisplayName = "no set, no pinset line and no local items")]
    public void NoSet()
    {
        var plain = Inbox.Render(SetRender().Relay, 1);
        Assert.DoesNotContain("pinset", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("pin\t-1", plain, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "a long set name is cut where it stands in for the owner")]
    public void LongNameCut()
    {
        var set = new PinSet(Parsed("""{"name":"A very long pin set name indeed","items":{"pins":[{"area":1,"x":0,"y":0,"z":0}]}}"""));
        Assert.Contains("pin\t-1\tA very long pin set \t", Inbox.Render(SetRender().Relay, 1, set), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "moves and removes of pin-set items never reach the server")]
    public void LocalItemCommands()
    {
        var relay = new PinShareRelay { Channel = "secret" };
        Assert.Empty(relay.Consume(Lines(
            ["1", "remove", "-2"], ["2", "arrow_remove", "-4"],
            ["3", "move", "-1", "1", "2", "3"],
            ["4", "arrow_move", "-4", "0", "0", "0", "1", "1", "1"]), true));
        Assert.Single(relay.Consume(Lines(["5", "remove", "12"]), true));
    }

    [Fact(DisplayName = "save body files the channel's snapshot under the quest slug")]
    public void SaveBody()
    {
        var pins = PinShareJson.Items(Parsed("""[{"id":3,"owner":"Teapot","floor":10101,"x":1,"y":2,"z":3}]"""));
        var body = Parsed(PinSetSave.Body("ep1-towards-the-future", pins, null));
        Assert.Equal("ep1-towards-the-future", Get(body, "quest")!.Value.GetString());
        var items = Get(body, "items")!.Value;
        Assert.Equal(10101, Get(Get(items, "pins")!.Value[0], "floor")!.Value.GetInt64());
        Assert.Equal(0, Get(items, "arrows")!.Value.GetArrayLength());
        Assert.Null(Get(body, "name"));
    }

    [Fact(DisplayName = "an arrow from a pre-bend server gets its straight midpoint")]
    public void ArrowMidpoint()
    {
        var arrows = PinShareJson.Items(Parsed(
            """
            [{"floor":1,"x1":0,"y1":0,"z1":0,"x2":4,"y2":2,"z2":-6},
             {"floor":1,"x1":0,"y1":0,"z1":0,"x2":4,"y2":2,"z2":-6,"xm":9,"ym":9,"zm":9}]
            """));
        var saved = Get(Get(Parsed(PinSetSave.Body("q", [], arrows)), "items")!.Value, "arrows")!.Value;
        Assert.Equal(2d, Get(saved[0], "xm")!.Value.GetDouble());
        Assert.Equal(1d, Get(saved[0], "ym")!.Value.GetDouble());
        Assert.Equal(-3d, Get(saved[0], "zm")!.Value.GetDouble());
        Assert.Equal(9d, Get(saved[1], "xm")!.Value.GetDouble());
    }

    private static PinSetTracker LoadedTracker()
    {
        var tracker = new PinSetTracker(_ => ["x"], () => true);
        Assert.NotNull(tracker.FetchWanted(new PinShareQuest(1234, "Towards the Future")));
        Assert.True(tracker.Land(1234, new PinSet(Parsed(TtfSet))));
        return tracker;
    }

    [Fact(DisplayName = "a failed read (NIL snapshot) keeps the set and the load")]
    public void FailedReadKeeps()
    {
        var tracker = LoadedTracker();
        var set = tracker.Current;
        Assert.Null(tracker.FetchWanted(null));
        Assert.Same(set, tracker.Current);
        Assert.Equal(1234, tracker.FetchPtr);
    }

    [Fact(DisplayName = "no quest loaded forgets the set, the slugs and the load")]
    public void NoQuestForgets()
    {
        var tracker = LoadedTracker();
        Assert.Null(tracker.FetchWanted(new PinShareQuest(0, null)));
        Assert.Null(tracker.Current);
        Assert.Null(tracker.QuestSlugs);
        Assert.Null(tracker.FetchPtr);
    }

    // --- staged rollout ----------------------------------------------------

    [Fact(DisplayName = "an older server's /api/me (no features) and no account mean no")]
    public void FeatureGate()
    {
        Assert.False(PinShareSettings.FeatureAllowed(Parsed("""{"id":1,"role":"user"}""")));
        Assert.False(PinShareSettings.FeatureAllowed(Parsed("""{"features":"pinshare"}""")));
        Assert.False(PinShareSettings.FeatureAllowed(null));
        Assert.True(PinShareSettings.FeatureAllowed(Parsed("""{"features":["pinshare"]}""")));
    }

    [Fact(DisplayName = "every relay status has a line in both languages")]
    public void StatusTexts()
    {
        PinShareStatus[] statuses =
        [
            new(PinShareStatusKind.Off), new(PinShareStatusKind.NotAllowed), new(PinShareStatusKind.NoChannel),
            new(PinShareStatusKind.WaitingGame), new(PinShareStatusKind.Connecting),
            new(PinShareStatusKind.Connected, "secret", 2), new(PinShareStatusKind.ConnectedNoAddon),
            new(PinShareStatusKind.LocalOnly, "TTF route"), new(PinShareStatusKind.Error, "boom"),
            new(PinShareStatusKind.NoAddonPlugin), new(PinShareStatusKind.InstallFailed, "denied"),
            new(PinShareStatusKind.BrokenLink, @"C:\Games\addons\Pin Share"), new(PinShareStatusKind.Conflict),
        ];
        Assert.Equal(Enum.GetValues<PinShareStatusKind>().Length, statuses.Length);
        foreach (var status in statuses)
        {
            foreach (var language in Languages.All)
                Assert.NotEmpty(status.Describe(language).Text);
        }
    }
}

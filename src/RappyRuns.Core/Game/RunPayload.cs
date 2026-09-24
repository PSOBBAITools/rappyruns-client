using System.Globalization;
using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Game;

/// <summary>
/// The POST /api/runs body built from a detector run plist
/// (api-client.lisp:534 run-json, :445 telemetry-json) - a <b>contract</b>
/// (spec core §7.2/§7.3): the key set is pinned by tests, optional keys ride
/// only when the Lisp value is non-NIL (0 and "" included), booleans only
/// when true, and single floats print the way jzon prints them. Works on any
/// run plist - fresh from the <see cref="Detector"/> or read back from a Lisp
/// queue.sexp.
/// </summary>
public static class RunPayload
{
    /// <summary>api-client.lisp:534 run-json: the request body as the Lisp client would send it.</summary>
    public static string RunJson(Plist run) => RunJsonObject(run).Stringify();

    public static JObject RunJsonObject(Plist run)
    {
        var o = new JObject
        {
            ["quest"] = Value(run.Get("quest-slug")),
            ["time_ms"] = Value(run.Get("time-ms")),
            ["party_size"] = Value(run.Get("party-size")),
        };
        // The server defaults pb to false; only send it when true.
        if (Truthy(run.Get("pb"))) o["pb"] = LispJson.True;
        if (Truthy(run.Get("episode"))) o["episode"] = Value(run.Get("episode"));
        if (Truthy(run.Get("difficulty"))) o["difficulty"] = Value(run.Get("difficulty"));
        if (Truthy(run.Get("death-count"))) o["death_count"] = Value(run.Get("death-count"));
        if (Truthy(run.Get("aborted"))) o["aborted"] = LispJson.True;
        // Sandbox or normal; the name colour itself is never part of the run (spec core §13).
        if (Truthy(run.Get("account-mode"))) o["account_mode"] = Value(run.Get("account-mode"));
        if (Truthy(run.Get("unranked"))) o["unranked"] = LispJson.True;
        if (Truthy(run.Get("run-private"))) o["private"] = LispJson.True;
        if (Truthy(run.Get("submitter-section-id"))) o["submitter_section_id"] = Value(run.Get("submitter-section-id"));
        var players = new JArray();
        foreach (var node in Elements(run.Get("players")))
        {
            var p = Plist.From(node) ?? new Plist();
            var entry = new JObject
            {
                ["name"] = Value(p.Get("name")),
                ["class"] = Value(p.Get("class")),
            };
            if (Truthy(p.Get("level"))) entry["level"] = Value(p.Get("level"));
            if (Truthy(p.Get("section-id"))) entry["section_id"] = Value(p.Get("section-id"));
            if (Truthy(p.Get("guild-card"))) entry["guild_card"] = Value(p.Get("guild-card"));
            players.Items.Add(entry);
        }
        o["players"] = players;
        // (getf run :quest-name (getf run :quest-slug)): a present NIL prints "NIL".
        var name = run.Contains("quest-name") ? run.Get("quest-name") : run.Get("quest-slug");
        o["notes"] = LispJson.Str("Auto-submitted by ephinea-ta-client (" + Princ(name) +
                                  (Truthy(run.Get("aborted")) ? ", aborted" : "") +
                                  (Truthy(run.Get("unranked")) ? ", record only" : "") + ")");
        if (Truthy(run.Get("telemetry"))) o["telemetry"] = TelemetryJsonObject(run.Get("telemetry")!);
        return o;
    }

    /// <summary>api-client.lisp:445 telemetry-json: the telemetry plist (telemetry-run-data) as JSON.</summary>
    public static string TelemetryJson(SexpNode data) => TelemetryJsonObject(data).Stringify();

    public static JObject TelemetryJsonObject(SexpNode data)
    {
        var d = Plist.From(data) ?? new Plist();
        var o = new JObject
        {
            ["frame_keys"] = new JArray(Telemetry.FrameKeys.Select(k => (LispJson)LispJson.Str(k)).ToList()),
            ["frames"] = new JArray(Elements(d.Get("frames")).Select(FrameJson).ToList()),
            ["death_count"] = OrZero(d.Get("death-count")),
            ["kills"] = OrZero(d.Get("kills")),
            ["meseta_charged"] = OrZero(d.Get("meseta-charged")),
            ["tp_used"] = OrZero(d.Get("tp-used")),
            ["items_used"] = AlistJson(d.Get("items-used"), k => k.KeywordName is { } n ? n.ToLowerInvariant().Replace('-', '_') : Princ(k)),
            ["techs_cast"] = AlistJson(d.Get("techs-cast"), Princ),
            ["time_by_state"] = AlistJson(d.Get("time-by-state"), Princ),
        };
        // traps-used is a plist (:dt n :ft n :ct n) turned into an alist.
        var traps = Elements(d.Get("traps-used"));
        var trapPairs = new List<SexpNode>();
        for (var i = 0; i + 1 < traps.Count; i += 2) trapPairs.Add(new SList([traps[i]], traps[i + 1]));
        o["traps_used"] = AlistJson(trapPairs.Count == 0 ? SexpNode.Nil : new SList(trapPairs), k => (k.KeywordName ?? Princ(k)).ToLowerInvariant());
        var weapons = new JArray();
        foreach (var node in Elements(d.Get("weapons")))
        {
            var w = Plist.From(node) ?? new Plist();
            weapons.Items.Add(new JObject
            {
                ["id"] = Value(w.Get("id")),
                ["type"] = LispJson.Str((w.Get("type") is { } t ? t.KeywordName ?? Princ(t) : "WEAPON").ToLowerInvariant()),
                ["display"] = Value(w.Get("display")),
                ["seconds"] = w.Get("seconds") is { } s ? Value(s) : LispJson.Int(0),
                ["attacks"] = w.Get("attacks") is { } a ? Value(a) : LispJson.Int(0),
                ["techs"] = w.Get("techs") is { } te ? Value(te) : LispJson.Int(0),
            });
        }
        o["weapons"] = weapons;
        var events = new JArray();
        foreach (var node in Elements(d.Get("events")))
        {
            var e = Plist.From(node) ?? new Plist();
            var entry = new JObject { ["t"] = Value(e.Get("t")), ["type"] = Value(e.Get("type")) };
            // floor 0 / room 0 are non-NIL in Lisp, so they travel.
            if (Truthy(e.Get("floor"))) entry["floor"] = Value(e.Get("floor"));
            if (Truthy(e.Get("room"))) entry["room"] = Value(e.Get("room"));
            if (Truthy(e.Get("ms"))) entry["ms"] = Value(e.Get("ms"));
            events.Items.Add(entry);
        }
        o["events"] = events;
        if (Truthy(d.Get("track")))
            o["track"] = new JArray(Elements(d.Get("track")).Select(row => (LispJson)new JArray(Elements(row).Select(Value).ToList())).ToList());
        if (Truthy(d.Get("monsters"))) o["monsters"] = MonstersJson(d.Get("monsters")!);
        if (Truthy(d.Get("bosses"))) o["bosses"] = BossesJson(d.Get("bosses")!);
        o["player_damage"] = AlistJson(d.Get("player-damage"), Princ);
        o["last_hits"] = AlistJson(d.Get("last-hits"), Princ);
        o["monster_hp_pool"] = new JArray(Elements(d.Get("monster-hp-pool")).Select(Value).ToList());
        if (Truthy(d.Get("max-party-pb-shifta"))) o["max_party_pb_shifta"] = Value(d.Get("max-party-pb-shifta"));
        if (Truthy(d.Get("illegal-shifta"))) o["illegal_shifta"] = LispJson.True;
        if (Truthy(d.Get("fast-warps"))) o["fast_warps"] = LispJson.True;
        return o;
    }

    /// <summary>api-client.lisp:403 frame-json: a frame row; the location columns become objects.</summary>
    private static LispJson FrameJson(SexpNode frame)
    {
        var values = Elements(frame);
        var row = new JArray();
        for (var i = 0; i < values.Count && i < Telemetry.FrameKeys.Count; i++)
        {
            var key = Telemetry.FrameKeys[i];
            row.Items.Add(key is "player_locs" or "monster_locs" ? LocsJson(values[i]) : Value(values[i]));
        }
        return row;
    }

    /// <summary>api-client.lisp:396 locs-json: rows (key . values) -> {princ(key): [values]}.</summary>
    private static JObject LocsJson(SexpNode entries)
    {
        var o = new JObject();
        foreach (var entry in Elements(entries))
        {
            var items = Elements(entry);
            if (items.Count == 0) continue;
            o[Princ(items[0])] = new JArray(items.Skip(1).Select(Value).ToList());
        }
        return o;
    }

    /// <summary>api-client.lisp:414 monsters-json (psostats QuestRun.Monsters).</summary>
    private static JArray MonstersJson(SexpNode monsters)
    {
        var array = new JArray();
        foreach (var node in Elements(monsters))
        {
            var m = Plist.From(node) ?? new Plist();
            var entry = new JObject
            {
                ["id"] = Value(m.Get("id")),
                ["unitxt_id"] = Value(m.Get("unitxt")),
                ["spawn_ms"] = Value(m.Get("spawn-ms")),
            };
            if (Truthy(m.Get("name"))) entry["name"] = Value(m.Get("name"));
            if (Truthy(m.Get("killed-ms"))) entry["killed_ms"] = Value(m.Get("killed-ms"));
            if (Truthy(m.Get("frame1"))) entry["frame1"] = LispJson.True;
            array.Items.Add(entry);
        }
        return array;
    }

    /// <summary>api-client.lisp:432 bosses-json: keyed by monster id (psostats Bosses).</summary>
    private static JObject BossesJson(SexpNode bosses)
    {
        var o = new JObject();
        foreach (var node in Elements(bosses))
        {
            var b = Plist.From(node) ?? new Plist();
            var entry = new JObject
            {
                ["name"] = Value(b.Get("name")),
                ["unitxt_id"] = Value(b.Get("unitxt")),
                ["spawn_t"] = Value(b.Get("spawn-t")),
                ["hp"] = new JArray(Elements(b.Get("hp")).Select(Value).ToList()),
            };
            if (Truthy(b.Get("killed-t"))) entry["killed_t"] = Value(b.Get("killed-t"));
            o[Princ(b.Get("id"))] = entry;
        }
        return o;
    }

    /// <summary>api-client.lisp:384 alist-json: alist -> object, dropping NIL and non-positive counts.</summary>
    private static JObject AlistJson(SexpNode? alist, Func<SexpNode, string> key)
    {
        var o = new JObject();
        foreach (var cell in Elements(alist))
        {
            if (cell is not SList { Items.Count: >= 1 } pair) continue;
            var count = pair.Tail ?? (pair.Items.Count > 1 ? new SList(pair.Items.Skip(1).ToList()) : SexpNode.Nil);
            if (count.AsNumber is { } n && n > 0) o[key(pair.Items[0])] = Value(count);
        }
        return o;
    }

    private static LispJson OrZero(SexpNode? node) => Truthy(node) ? Value(node) : LispJson.Int(0);

    /// <summary>Lisp truthiness: only NIL (or an absent key) is false.</summary>
    public static bool Truthy(SexpNode? node) => node is not null && node.IsTrue;

    private static IReadOnlyList<SexpNode> Elements(SexpNode? node) =>
        node is null || node.IsNil ? [] : node is SList { Tail: null } l ? l.Items : node is SVector v ? v.Items : [];

    /// <summary>A Lisp datum as jzon writes it: NIL false, T true, numbers typed, keywords by name.</summary>
    public static LispJson Value(SexpNode? node) => node switch
    {
        null => LispJson.False,
        _ when node.IsNil => LispJson.False,
        SSymbol { IsTSymbol: true } => LispJson.True,
        SString s => LispJson.Str(s.Value),
        SInteger i => LispJson.Int(i.Value),
        SFloat { IsDouble: false } f => LispJson.Single((float)f.Value),
        SFloat f => new JDouble(f.Value),
        SKeyword k => LispJson.Str(k.Name.ToLowerInvariant()),
        SSymbol sym => LispJson.Str(sym.Name.ToLowerInvariant()),
        SList l => new JArray(l.Items.Select(Value).ToList()),
        SVector v => new JArray(v.Items.Select(Value).ToList()),
        _ => LispJson.False,
    };

    /// <summary>princ-to-string for the atoms that key JSON objects.</summary>
    public static string Princ(SexpNode? node) => node switch
    {
        null => "NIL",
        _ when node.IsNil => "NIL",
        SString s => s.Value,
        SInteger i => i.Value.ToString(CultureInfo.InvariantCulture),
        SKeyword k => k.Name,
        SSymbol sym => sym.Name,
        _ => SexpWriter.Write(node),
    };
}

/// <summary>A Lisp double-float (only ever from an old queue file); jzon's write-double layout.</summary>
public sealed record JDouble(double Value) : LispJson
{
    internal override void Write(System.Text.StringBuilder sb) =>
        sb.Append(SexpWriter.FormatFloat(new SFloat(Value, IsDouble: true)).Replace("d0", "", StringComparison.Ordinal).Replace('d', 'e'));
}

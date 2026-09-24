using System.Globalization;

namespace RappyRuns.Core.Game;

/// <summary>A kill for the quest-rule form (trigger-log.lisp:112 *last-kill*).</summary>
public sealed record LastKill(int Id, string? Name, long? Unitxt);

/// <summary>The quest the run logs belong to (trigger-log.lisp:157 *run-quest*): number, name, episode.</summary>
public sealed record RunQuest(int? Number, string? Name, int? Episode);

/// <summary>A kill in the run log: the local player's floor/room at the time, the loaded map, a tick for ordering.</summary>
public sealed record KillLogEntry(int Id, string? Name, long? Unitxt, int? Floor, int? Room, int? Map, long Time);

/// <summary>A floor switch that flipped 0→1, tagged with the local player's room.</summary>
public sealed record SwitchLogEntry(int Floor, int Switch, int? Room, long Time);

/// <summary>A room of the run (trigger-log.lisp:213 run-rooms), rooms in first-entered order.</summary>
public sealed record RunRoom(int? Floor, int? Room, int? Map, IReadOnlyList<KillLogEntry> Kills, KillLogEntry LastKill, SwitchLogEntry? Switch);

/// <summary>One pickable row of the Rooms list (trigger-log.lisp:276 run-room-rows).</summary>
public sealed record RoomRow(string Area, RoomRowKind Kind, string? Name, Trigger Trigger);

public enum RoomRowKind
{
    /// <summary>"Clear this room": the door floor switch, else the room's last enemy dying.</summary>
    Clear,

    /// <summary>A specific enemy's kill.</summary>
    Enemy,
}

/// <summary>
/// Frame-diff helpers behind the trigger log and the quest-rule registration
/// (trigger-log.lisp:65-308): newly killed monsters, floor-switch flips, the
/// last kill, and the per-load kill/switch logs grouped into rooms. Owned by
/// the poll thread; the UI reads <see cref="LastKill"/>, <see cref="RunQuest"/>
/// and <see cref="RoomRows"/> (immutable snapshots, safe to read from any thread).
/// </summary>
public sealed class RunLogs(IGameClock clock)
{
    private volatile IReadOnlyList<KillLogEntry> _kills = []; // oldest first
    private volatile IReadOnlyList<SwitchLogEntry> _switches = []; // oldest first

    /// <summary>The most recently killed enemy of this quest load, or null.</summary>
    public LastKill? LastKill { get; private set; }

    /// <summary>The quest of the current/last run logs; kept into the lobby.</summary>
    public RunQuest? RunQuest { get; private set; }

    public IReadOnlyList<KillLogEntry> Kills => _kills;

    public IReadOnlyList<SwitchLogEntry> Switches => _switches;

    /// <summary>
    /// trigger-log.lisp:65 newly-killed-monsters: monsters alive (hp&gt;0) in
    /// <paramref name="previous"/> and at 0 hp now, in the current frame's order.
    /// </summary>
    public static List<MonsterState> NewlyKilledMonsters(Snapshot? previous, Snapshot? snapshot)
    {
        var result = new List<MonsterState>();
        var old = previous?.Monsters ?? [];
        foreach (var monster in snapshot?.Monsters ?? [])
        {
            var was = old.FirstOrDefault(m => m.Id == monster.Id);
            if (was is not null && (was.Hp ?? 0) > 0 && (monster.Hp ?? 0) == 0) result.Add(monster);
        }
        return result;
    }

    /// <summary>
    /// trigger-log.lisp:79 map-floor-switch-diffs: every differing switch bit as
    /// (floor, switch, oldOn, newOn), bytes then bits MSB first.
    /// </summary>
    public static IEnumerable<(int Floor, int Switch, bool OldOn, bool NewOn)> FloorSwitchDiffs(Snapshot? previous, Snapshot? snapshot)
    {
        var old = previous?.FloorSwitches;
        var now = snapshot?.FloorSwitches;
        if (old is null || now is null) yield break;
        for (var i = 0; i < Math.Min(old.Length, now.Length); i++)
        {
            if (old[i] == now[i]) continue;
            for (var bit = 0; bit < 8; bit++)
            {
                var mask = 0x80 >> bit;
                if ((old[i] & mask) != (now[i] & mask))
                    yield return (i / 32, 8 * (i % 32) + bit, (old[i] & mask) != 0, (now[i] & mask) != 0);
            }
        }
    }

    /// <summary>trigger-log.lisp:100 newly-set-floor-switches: the 0→1 flips.</summary>
    public static List<(int Floor, int Switch)> NewlySetFloorSwitches(Snapshot? previous, Snapshot? snapshot) =>
        FloorSwitchDiffs(previous, snapshot).Where(d => !d.OldOn && d.NewOn).Select(d => (d.Floor, d.Switch)).ToList();

    /// <summary>
    /// trigger-log.lisp:120 update-last-kill: forget the kill when no quest is
    /// loaded or the quest pointer changed; otherwise keep the frame's last kill.
    /// </summary>
    public void UpdateLastKill(Snapshot? previous, Snapshot? snapshot)
    {
        if (snapshot is null || !snapshot.QuestLoaded)
        {
            LastKill = null;
            return;
        }
        if (previous?.QuestPtr is { } prevPtr && prevPtr != snapshot.QuestPtr)
        {
            LastKill = null;
            return;
        }
        if (NewlyKilledMonsters(previous, snapshot).LastOrDefault() is { } killed)
            LastKill = new LastKill(killed.Id, killed.Name, killed.Unitxt);
    }

    /// <summary>
    /// trigger-log.lisp:166 update-run-logs: accumulate kills and switch flips
    /// tagged with the local player's floor/room; reset on a fresh load; keep
    /// everything while no quest is loaded (registration from the lobby).
    /// </summary>
    public void UpdateRunLogs(Snapshot? previous, Snapshot? snapshot)
    {
        if (snapshot?.QuestPtr is not > 0) return;
        var ptr = snapshot.QuestPtr;
        if (previous is null || previous.QuestPtr != ptr)
        {
            _kills = [];
            _switches = [];
            RunQuest = new RunQuest(snapshot.QuestNumber, snapshot.QuestName, snapshot.Episode);
        }
        var me = snapshot.MyPlayer;
        var floor = me?.Floor;
        var room = me?.Room;
        var map = snapshot.Map;
        var time = clock.Now;
        var newKills = NewlyKilledMonsters(previous, snapshot);
        if (newKills.Count > 0)
            _kills = _kills.Concat(newKills.Select(m => new KillLogEntry(m.Id, m.Name, m.Unitxt, floor, room, map, time))).ToList();
        var flips = NewlySetFloorSwitches(previous, snapshot);
        if (flips.Count > 0)
            _switches = _switches.Concat(flips.Select(s => new SwitchLogEntry(s.Floor, s.Switch, room, time))).ToList();
    }

    /// <summary>
    /// trigger-log.lisp:199 room-clear-switch: the switch in the same player room
    /// nearest in time to the room's last kill. The Lisp walks the log newest
    /// first and keeps the first strictly-nearer one, so ties go to the newest.
    /// </summary>
    private SwitchLogEntry? RoomClearSwitch(int? room, KillLogEntry? lastKill)
    {
        SwitchLogEntry? best = null;
        long bestDist = 0;
        for (var i = _switches.Count - 1; i >= 0; i--)
        {
            var sw = _switches[i];
            if (sw.Room != room) continue;
            var dist = lastKill is not null ? Math.Abs(sw.Time - lastKill.Time) : 0;
            if (best is null || dist < bestDist)
            {
                best = sw;
                bestDist = dist;
            }
        }
        return best;
    }

    /// <summary>trigger-log.lisp:213 run-rooms: kills grouped by (floor, room) in first-seen order.</summary>
    public List<RunRoom> RunRooms()
    {
        var groups = new List<(int? Floor, int? Room, int? Map, List<KillLogEntry> Kills)>();
        foreach (var kill in _kills)
        {
            var at = groups.FindIndex(g => g.Floor == kill.Floor && g.Room == kill.Room);
            if (at >= 0) groups[at].Kills.Add(kill);
            else groups.Add((kill.Floor, kill.Room, kill.Map, [kill]));
        }
        return groups.Select(g =>
        {
            var last = g.Kills[^1];
            return new RunRoom(g.Floor, g.Room, g.Map, g.Kills, last, RoomClearSwitch(g.Room, last));
        }).ToList();
    }

    /// <summary>trigger-log.lisp:247 +map-names+ (pinned equal to the server's views.lisp by a contract test).</summary>
    public static IReadOnlyList<string> MapNames { get; } =
    [
        "Pioneer II", "Forest 1", "Forest 2", "Cave 1", "Cave 2", "Cave 3",
        "Mine 1", "Mine 2", "Ruins 1", "Ruins 2", "Ruins 3",
        "Under the Dome", "Underground Channel", "Control Room", "????",
        "Lobby", "BA Spaceship", "BA Temple", "Lab",
        "Temple Alpha", "Temple Beta 2", "Spaceship Alpha", "Spaceship Beta",
        "CCA", "Jungle North", "Jungle East", "Mountain", "Seaside",
        "Seabed Upper", "Seabed Lower", "Cliffs of Gal Da Val",
        "Test Subject Disposal Area", "Temple Final", "Spaceship Final",
        "Seaside at Night", "Control Tower",
        "Crater East", "Crater West", "Crater South", "Crater North",
        "Crater Interior", "Desert 1", "Desert 2", "Desert 3",
        "Meteor Impact Site", "Pioneer II",
    ];

    /// <summary>trigger-log.lisp:261 client-map-name: area name, or "Map N" out of range ("Map NIL" for none).</summary>
    public static string ClientMapName(int? number) =>
        number is { } n && n >= 0 && n < MapNames.Count
            ? MapNames[n]
            : "Map " + (number?.ToString(CultureInfo.InvariantCulture) ?? "NIL");

    /// <summary>trigger-log.lisp:267 room-area-label: "&lt;area&gt; · room &lt;n&gt;", or "Room &lt;n&gt;" without a map.</summary>
    public static string RoomAreaLabel(int? map, int? room)
    {
        var rm = room?.ToString(CultureInfo.InvariantCulture) ?? "NIL";
        return map is not null ? $"{ClientMapName(map)} · room {rm}" : $"Room {rm}";
    }

    /// <summary>
    /// trigger-log.lisp:276 run-room-rows: per room a Clear row (door switch, else
    /// the last enemy), then one Enemy row per distinct enemy in kill order.
    /// </summary>
    public List<RoomRow> RoomRows()
    {
        var rows = new List<RoomRow>();
        foreach (var room in RunRooms())
        {
            var area = RoomAreaLabel(room.Map, room.Room);
            if (room.Switch is { } sw) rows.Add(new RoomRow(area, RoomRowKind.Clear, null, new FloorSwitchTrigger(sw.Floor, sw.Switch)));
            else rows.Add(new RoomRow(area, RoomRowKind.Clear, null, new MonsterDeadTrigger(room.LastKill.Id)));
            var seen = new HashSet<int>();
            foreach (var k in room.Kills)
            {
                if (seen.Add(k.Id)) rows.Add(new RoomRow(area, RoomRowKind.Enemy, k.Name, new MonsterDeadTrigger(k.Id)));
            }
        }
        return rows;
    }
}

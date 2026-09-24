using System.Text.Json;

namespace RappyRuns.Core.Ghost;

/// <summary>How precise the reference run's room splits are (ghost.lisp:46).</summary>
public enum GhostPrecision
{
    /// <summary>Frame-derived splits, second-accurate: gaps show whole seconds.</summary>
    Sec,

    /// <summary>Room events stamped in ms.</summary>
    Ms,
}

/// <summary>
/// One room visit of the reference run, in progression order (ghost.lisp:118-129).
/// Integer slots are null when the wire carried anything but an integer: the
/// Lisp matcher compares them with EQL against the live integers, so a
/// non-integer can never match there either.
/// </summary>
/// <param name="Kills">The reference's kills during this visit, or null when the
/// server does not annotate them. Null means unknown (show the split), not zero.</param>
public sealed record GhostRoom(long? Floor, long? Room, long? Nth, long? EnterMs, long? Kills);

/// <summary>
/// One row of the ghost's own position timeline (ghost.lisp:49-53): (ms floor
/// map x z [y]). <see cref="Y"/> is null on rows that predate the height column.
/// </summary>
public sealed record TrackRow(long Ms, long Floor, long Map, double X, double Z, double? Y);

/// <summary>
/// The reference run fetched for a quest load (the Lisp GHOST struct,
/// ghost.lisp:31). Immutable, so the fetch task can hand it to the poll
/// thread with one reference swap.
/// </summary>
/// <param name="QuestSlug">Slug of the reference run's quest category. A chosen
/// target can be a segment category of the same in-game quest; the completion
/// comparison keys off this slug.</param>
/// <param name="TimeMs">The ghost's final time: the number to beat.</param>
/// <param name="Label">Submitter name, for toasts and run-list notes.</param>
/// <param name="Source">"target" (chosen on the site) or "pb".</param>
/// <param name="Pb">The reference's board category 0/1, null from an older server.</param>
/// <param name="Rooms">Room visits in progression order; an element is null when
/// the wire element was not an object (Lisp keeps a NIL there).</param>
/// <param name="Track">Position rows oldest first, or null when the payload had no track.</param>
public sealed record GhostReference(
    string? QuestSlug,
    long? RunId,
    long TimeMs,
    string? Label,
    string? Source,
    long? Pb,
    GhostPrecision Precision,
    IReadOnlyList<GhostRoom?> Rooms,
    IReadOnlyList<TrackRow>? Track)
{
    /// <summary>
    /// GET /api/quests/:slug/ghost payload (raw JSON) to a ghost, or null when
    /// malformed (parse-ghost-splits, ghost.lisp:96). Unparseable JSON is null
    /// too: fetch-ghost-splits parses under IGNORE-ERRORS.
    /// </summary>
    public static GhostReference? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Parse(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The parsed-payload form of <see cref="Parse(string?)"/>.</summary>
    public static GhostReference? Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (!payload.TryGetProperty("rooms", out var rooms) || rooms.ValueKind != JsonValueKind.Array) return null;
        var timeMs = Integer(payload, "time_ms");
        if (timeMs is null) return null;

        var parsedRooms = new List<GhostRoom?>(rooms.GetArrayLength());
        foreach (var room in rooms.EnumerateArray())
        {
            if (room.ValueKind != JsonValueKind.Object)
            {
                parsedRooms.Add(null);
                continue;
            }
            parsedRooms.Add(new GhostRoom(
                Integer(room, "floor"),
                Integer(room, "room"),
                Integer(room, "nth"),
                // Lisp only asks NUMBERP here; the server always sends an
                // integer. A fractional one is rounded (deviation: Lisp would
                // carry a float gap forward).
                RoundedNumber(room, "enter_ms"),
                // (round kills): banker's rounding, like Math.Round's default.
                RoundedNumber(room, "kills")));
        }

        return new GhostReference(
            QuestSlug: String(payload, "quest"),
            RunId: Integer(payload, "run_id"),
            TimeMs: timeMs.Value,
            Label: String(payload, "submitter"),
            Source: String(payload, "source"),
            Pb: Integer(payload, "pb"),
            Precision: String(payload, "precision") == "ms" ? GhostPrecision.Ms : GhostPrecision.Sec,
            Rooms: parsedRooms,
            Track: ParseTrack(payload));
    }

    /// <summary>
    /// The track rows (ghost.lisp:130-155): each row an array of 5-6 numbers;
    /// anything else is dropped. The height column rides out-of-band in
    /// "track_y" (server hunt:wire-track keeps rows 5-wide so v0.51/v0.52
    /// clients keep their course map) and is zipped back on by the RAW track
    /// index, so a dropped malformed row cannot shift later heights.
    /// </summary>
    private static List<TrackRow>? ParseTrack(JsonElement payload)
    {
        if (!payload.TryGetProperty("track", out var track) || track.ValueKind != JsonValueKind.Array) return null;
        JsonElement? heights = payload.TryGetProperty("track_y", out var h) && h.ValueKind == JsonValueKind.Array ? h : null;
        var heightCount = heights?.GetArrayLength() ?? 0;
        var rows = new List<TrackRow>();
        var i = 0;
        foreach (var row in track.EnumerateArray())
        {
            var index = i++;
            if (row.ValueKind != JsonValueKind.Array) continue;
            var n = row.GetArrayLength();
            if (n < 5 || n > 6) continue;
            if (row.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Number)) continue;
            // ms / floor / map are integers on the wire; the Lisp row keeps
            // whatever number came, but a fractional one there would never
            // EQL-match a live floor anyway. Such a row is dropped (deviation).
            if (!row[0].TryGetInt64(out var ms) || !row[1].TryGetInt64(out var floor) || !row[2].TryGetInt64(out var map))
                continue;
            double? y = n == 6 ? row[5].GetDouble() : null;
            if (y is null && heights is { } hs && index < heightCount && hs[index].ValueKind == JsonValueKind.Number)
                y = hs[index].GetDouble();
            rows.Add(new TrackRow(ms, floor, map, row[3].GetDouble(), row[4].GetDouble(), y));
        }
        return rows;
    }

    private static long? Integer(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) ? v : null;

    private static long? RoundedNumber(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Number) return null;
        return e.TryGetInt64(out var v) ? v : (long)Math.Round(e.GetDouble());
    }

    private static string? String(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}

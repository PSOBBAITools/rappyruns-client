namespace RappyRuns.Core.Ghost;

/// <summary>
/// One matched-room split for the overlay's room list (ghost.lisp:62-65):
/// the room entered, when (quest telemetry clock) and the cumulative gap then.
/// </summary>
public sealed record GhostSplit(long Room, long Floor, long Ms, long Delta);

/// <summary>
/// Live race state while a quest runs (the Lisp GHOST-RACE struct,
/// ghost.lisp:56). Owned by the poll thread: only <see cref="GhostOverlayData"/>
/// snapshots (immutable) cross to the overlay thread.
/// </summary>
/// <remarks>
/// The gap updates at room entries, matched by a streaming version of the
/// compare page's greedy room alignment: a cursor walks the ghost's room list in
/// progression order, and each live room entry claims the ghost's next visit of
/// that (floor, room) within a bounded lookahead. Order-based matching (rather
/// than strict nth-visit counting) keeps old second-precision ghosts usable:
/// their 1 Hz frames can miss a room crossed in under a second, and a
/// late-arriving fetch can join mid-route - both just skip unmatched entries
/// instead of desyncing the rest of the run (ghost.lisp:12-19).
/// </remarks>
public sealed class GhostRace
{
    /// <summary>
    /// How many ghost room entries past the cursor a live room entry may claim
    /// (+ghost-match-lookahead+, ghost.lisp:73). Bounds the damage of a route
    /// deviation: a detour can skip at most this many entries.
    /// </summary>
    public const int MatchLookahead = 15;

    private readonly List<GhostSplit> _splitsOldestFirst = [];
    private (long Floor, long Room)? _lastKey;

    public GhostRace(GhostReference? ghost = null) => Ghost = ghost;

    /// <summary>The attached ghost, or null while the fetch is in flight.</summary>
    public GhostReference? Ghost { get; set; }

    /// <summary>Index into the ghost's rooms of the next unclaimed entry.</summary>
    public int Cursor { get; private set; }

    /// <summary>Latest gap (live minus ghost), or null before the first match.</summary>
    public long? DeltaMs { get; set; }

    public int MatchedRooms { get; private set; }

    /// <summary>The live player's floor for the marker (null until first seen).</summary>
    public long? OwnFloor { get; private set; }

    /// <summary>The live player's height: the marker's fallback for tracks without heights.</summary>
    public float? OwnY { get; private set; }

    /// <summary>Matched-room history, newest first (as the Lisp list).</summary>
    public IReadOnlyList<GhostSplit> Splits
    {
        get
        {
            var copy = new GhostSplit[_splitsOldestFirst.Count];
            for (var i = 0; i < copy.Length; i++) copy[i] = _splitsOldestFirst[copy.Length - 1 - i];
            return copy;
        }
    }

    /// <summary>Replaces the split history (tests; mirrors a SETF of the slot). <paramref name="newestFirst"/> as the Lisp list.</summary>
    public void SetSplits(IEnumerable<GhostSplit> newestFirst)
    {
        _splitsOldestFirst.Clear();
        _splitsOldestFirst.AddRange(newestFirst.Reverse());
    }

    /// <summary>
    /// Feed the submitter's current (floor, room) at <paramref name="elapsedMs"/>
    /// on the quest telemetry's clock (ghost-race-note-room, ghost.lisp:151).
    /// On entry into a new room, claim the ghost's next visit of that room from
    /// the cursor onward (bounded lookahead), update the gap and record the
    /// split - unless the reference killed nothing there (a pass-through
    /// corridor; unknown kills still record). Rooms the ghost never entered, or
    /// entered outside the window, leave the previous gap standing and record no
    /// split. Returns the race's current gap.
    /// </summary>
    public long? NoteRoom(long floor, long room, long elapsedMs)
    {
        var key = (floor, room);
        if (_lastKey != key)
        {
            _lastKey = key;
            var rooms = Ghost?.Rooms;
            // (when rooms ...): an empty vector is still non-NIL, the loop
            // just has nothing to visit.
            if (rooms is not null)
            {
                var end = Math.Min(rooms.Count, Cursor + MatchLookahead);
                for (var i = Cursor; i < end; i++)
                {
                    var entry = rooms[i];
                    if (entry is { EnterMs: { } enterMs } && entry.Floor == floor && entry.Room == room)
                    {
                        var delta = elapsedMs - enterMs;
                        Cursor = i + 1;
                        DeltaMs = delta;
                        MatchedRooms++;
                        if (entry.Kills is null || entry.Kills > 0)
                            _splitsOldestFirst.Add(new GhostSplit(room, floor, elapsedMs, delta));
                        break;
                    }
                }
            }
        }
        return DeltaMs;
    }

    /// <summary>
    /// Track the submitter's own floor and height for the in-world marker
    /// (ghost-race-note-position, ghost.lisp:228): the floor gates the marker
    /// to same-floor ghosts, Y is its height fallback.
    /// </summary>
    public void NotePosition(long floor, float y)
    {
        OwnFloor = floor;
        OwnY = y;
    }
}

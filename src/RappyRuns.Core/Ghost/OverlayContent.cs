using RappyRuns.Core.I18n;

namespace RappyRuns.Core.Ghost;

/// <summary>Colors the overlay's second line: green ahead of the ghost, red behind, white before the first match.</summary>
public enum GhostDeltaState
{
    Neutral,
    Ahead,
    Behind,
}

/// <summary>
/// Everything the overlay thread paints, handed over in one immutable value
/// (the arguments of overlay-show!, overlay-win32.lisp:772). The overlay thread
/// never reads the config: corner, custom spot, marker toggle and language all
/// ride in here.
/// </summary>
/// <param name="Line1">The live clock, plus " REC" while recording.</param>
/// <param name="Line2">"vs &lt;ghost time&gt; &lt;gap&gt;", or null without a ghost.</param>
/// <param name="Ghost">The ghost panel snapshot, or null for the compact timer pill.</param>
/// <param name="Custom">The configured :overlay-position, read when <paramref name="Corner"/> is Custom.</param>
/// <param name="Language">For the split rows' "Room n" label.</param>
public sealed record OverlayContent(
    string Line1,
    string? Line2,
    GhostDeltaState DeltaState,
    GhostOverlayData? Ghost,
    OverlayCorner Corner,
    OverlayCustomPosition? Custom,
    Language Language)
{
    /// <summary>
    /// The content update-ghost-overlay builds at 4 Hz during a quest
    /// (overlay-win32.lisp:820-842).
    /// </summary>
    /// <param name="detectorElapsedMs">detector-elapsed-ms (the run clock; 0 when null).</param>
    /// <param name="recording">A recording is in progress: " REC" after the clock.</param>
    /// <param name="race">The live race, or null.</param>
    /// <param name="ghost">The ghost snapshot: <see cref="GhostSession.OverlayData"/> with the telemetry elapsed and the :ghost-marker setting, null without telemetry.</param>
    public static OverlayContent ForQuest(long? detectorElapsedMs, bool recording, GhostRace? race, GhostOverlayData? ghost,
        OverlayCorner corner, OverlayCustomPosition? custom, Language language)
    {
        var delta = race?.DeltaMs;
        return new OverlayContent(
            GhostFormat.RunTime(detectorElapsedMs ?? 0) + (recording ? " REC" : ""),
            GhostFormat.VsText(race),
            delta is null ? GhostDeltaState.Neutral : delta < 0 ? GhostDeltaState.Ahead : GhostDeltaState.Behind,
            ghost,
            corner,
            custom,
            language);
    }

    /// <summary>Show the ghost panel (vs header + room rows) only while a ghost is attached (overlay-ghost-active-p).</summary>
    public bool GhostActive => Ghost is not null;

    /// <summary>The panel height: 224 with the ghost panel, the 62 px pill without (overlay-current-height).</summary>
    public int PanelHeight => GhostActive ? OverlayLayout.GhostHeight : OverlayLayout.CompactHeight;

    /// <summary>The in-world marker is live: full-client-area window at 30 Hz (overlay-marker-wanted-p).</summary>
    public bool MarkerWanted => Ghost?.MarkerWanted ?? false;

    /// <summary>One split row's texts (overlay-draw-splits, overlay-win32.lisp:647): room label, enter clock, gap.</summary>
    public (string Room, string Clock, string Delta) SplitRow(GhostSplit split, GhostPrecision precision) =>
        (Strings.Default.Tr(Language, "overlay-room", split.Room),
         GhostFormat.SplitClock(split.Ms),
         GhostFormat.Delta(split.Delta, precision));
}

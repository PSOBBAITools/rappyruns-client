using System.Globalization;
using RappyRuns.Core.I18n;
using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Store;

/// <summary>A desktop toast's title and text, and the run page it opens when clicked.</summary>
public sealed record RunToast(string Title, string Text, string? Url);

/// <summary>
/// The display helpers for run entries (store.lisp:89-270, spec core §9.6):
/// time formats, the standing/ghost notes, the celebration toasts and the
/// runs-list Status and Video columns. Pure; the GUI state they depended on
/// in Lisp (the submission token, :video-upload, the in-flight upload) is
/// passed in.
/// </summary>
public static class RunDisplay
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Tr(Language language, string key, params object?[] args) =>
        Strings.Default.Tr(language, key, args);

    /// <summary>
    /// format-run-time: "m:ss.mmm". Past an hour this reads "65:00.000"
    /// where the site shows "1:05:00.000" (parked as backlog T20; parity).
    /// </summary>
    public static string FormatRunTime(long ms)
    {
        var totalSeconds = FloorDiv(ms, 1000);
        var millis = ms - totalSeconds * 1000;
        var minutes = FloorDiv(totalSeconds, 60);
        var seconds = totalSeconds - minutes * 60;
        return string.Create(Inv, $"{minutes}:{seconds:00}.{millis:000}");
    }

    /// <summary>format-split-clock: "m:ss", the seconds-precision clock.</summary>
    public static string FormatSplitClock(long ms)
    {
        var totalSeconds = FloorDiv(ms, 1000);
        var minutes = FloorDiv(totalSeconds, 60);
        return string.Create(Inv, $"{minutes}:{totalSeconds - minutes * 60:00}");
    }

    /// <summary>
    /// format-improvement-ms: a positive improvement as a compact hint -
    /// seconds with two decimals under a minute ("3.21s"), the m:ss.mmm clock
    /// above it. Parity: Lisp computes ms/1000 in single-float and FORMAT ~,2F
    /// rounds the float's exact binary value half away from zero, so 1005 ms
    /// (1.00499999 as a float) reads "1.00s" while 1125 ms reads "1.13s".
    /// </summary>
    public static string FormatImprovementMs(long ms)
    {
        if (ms >= 60000) return FormatRunTime(ms);
        var single = (float)ms / 1000f;
        // The double holds the float's exact value; decimal keeps 15+
        // significant digits, far finer than a float's distance to a tie.
        var rounded = Math.Round((decimal)(double)single, 2, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.00", Inv) + "s";
    }

    /// <summary>
    /// run-standing-note: the reward hint for a freshly submitted draft. Leads
    /// with the personal-best comparison; the provisional rank rides along
    /// only when another party shares the board ("#1 of 1" is noise). Null
    /// when the server sent no standing.
    /// </summary>
    public static string? RunStandingNote(Plist entry, Language language)
    {
        if (!(Val(entry, RunKeys.StandingRank) is { IsTrue: true } rank && Val(entry, RunKeys.StandingParties) is { IsTrue: true } parties))
            return null;
        var delta = Val(entry, RunKeys.StandingDeltaMs);
        var pb = delta.IsNil
            ? Tr(language, "standing-first")
            : delta.AsLong is < 0
                ? Tr(language, "standing-pb", FormatImprovementMs(-delta.AsLong!.Value))
                : Tr(language, "standing-behind", FormatImprovementMs(delta.AsLong ?? 0));
        return parties.AsLong >= 2
            ? $"{pb} · {Tr(language, "standing-rank", Aesthetic(rank), Aesthetic(parties))}"
            : pb;
    }

    /// <summary>ghost-note: "vs ghost -3.21s" when the entry carries an integer ghost delta.</summary>
    public static string? GhostNote(Plist entry, Language language)
    {
        if (Val(entry, RunKeys.GhostDeltaMs).AsLong is not { } delta) return null;
        return Tr(language, "ghost-note",
            delta < 0 ? "-" + FormatImprovementMs(-delta) : "+" + FormatImprovementMs(delta));
    }

    /// <summary>entry-note: the standing and ghost notes joined by " · ", or null.</summary>
    public static string? EntryNote(Plist entry, Language language)
    {
        var parts = new[] { RunStandingNote(entry, language), GhostNote(entry, language) }
            .Where(p => p is not null).ToList();
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    /// <summary>
    /// standing-toast: the celebration for a fresh submission's standing, or
    /// null. Only genuinely good news speaks, first match wins: provisional #1
    /// over another party, a top-3 place that beat somebody, a personal best,
    /// a first run on the board.
    /// </summary>
    public static (string Title, string Text)? StandingToast(Plist entry, Language language)
    {
        if (!(Val(entry, RunKeys.StandingRank) is { IsTrue: true } rankNode && Val(entry, RunKeys.StandingParties) is { IsTrue: true } partiesNode))
            return null;
        var rank = rankNode.AsLong ?? 0;
        var parties = partiesNode.AsLong ?? 0;
        var delta = Val(entry, RunKeys.StandingDeltaMs);
        var quest = QuestLabel(entry);
        var time = FormatRunTime(Val(entry, RunKeys.TimeMs).AsLong ?? 0);
        if (rank == 1 && parties >= 2)
            return (Tr(language, "toast-rank1-title"), Tr(language, "toast-rank1-text", quest, time));
        if (rank <= 3 && rank < parties)
            return (Tr(language, "toast-rank-title", rank),
                Tr(language, "toast-rank-text", quest, time, Tr(language, "standing-rank", rank, parties)));
        if (delta.AsLong is < 0)
            return (Tr(language, "toast-pb-title"),
                Tr(language, "toast-pb-text", quest, time, FormatImprovementMs(-delta.AsLong!.Value)));
        if (delta.IsNil)
            return (Tr(language, "toast-first-title"), Tr(language, "toast-first-text", quest, time));
        return null;
    }

    /// <summary>ghost-toast: when the entry beat its ghost, else null.</summary>
    public static (string Title, string Text)? GhostToast(Plist entry, Language language)
    {
        if (Val(entry, RunKeys.GhostDeltaMs).AsLong is not { } delta || delta >= 0) return null;
        return (Tr(language, "ghost-toast-title"),
            Tr(language, "ghost-toast-text", QuestLabel(entry),
                FormatRunTime(Val(entry, RunKeys.TimeMs).AsLong ?? 0), FormatImprovementMs(-delta)));
    }

    /// <summary>
    /// notify-standing-toasts' choice for one just-submitted entry: the
    /// standing toast, else the ghost toast, else null - one balloon per run.
    /// The caller skips this entirely when <c>:rank-toast</c> is off, and a
    /// click opens <see cref="RunToast.Url"/>.
    /// </summary>
    public static RunToast? ToastFor(Plist entry, Language language)
    {
        var toast = StandingToast(entry, language) ?? GhostToast(entry, language);
        return toast is { } t ? new RunToast(t.Title, t.Text, Val(entry, RunKeys.Url).AsString) : null;
    }

    /// <summary>
    /// run-status-label: the runs list's Status column.
    /// </summary>
    /// <param name="entry">The queue entry.</param>
    /// <param name="language">UI language.</param>
    /// <param name="hasSubmissionToken">Whether <c>submission-token</c> is non-empty; without one a queued run is parked.</param>
    /// <param name="videoUpload">The <c>:video-upload</c> setting (forced on in production).</param>
    public static string RunStatusLabel(Plist entry, Language language, bool hasSubmissionToken, bool videoUpload = true)
    {
        if (RunEntries.Is(entry, RunKeys.VideoAttached))
        {
            if (RunEntries.Is(entry, RunKeys.Aborted)) return Tr(language, "status-aborted-video-attached");
            if (RunEntries.Is(entry, RunKeys.Held)) return Tr(language, "status-video-held");
            if (RunEntries.Is(entry, RunKeys.Approved)) return Tr(language, "status-video-approved");
            return Tr(language, "status-video-attached");
        }
        switch (RunEntries.Status(entry))
        {
            case RunStatus.Queued:
                return Tr(language, hasSubmissionToken ? "status-queued" : "status-queued-unlinked");
            case RunStatus.Submitted:
                string key;
                if (RunEntries.Is(entry, RunKeys.Aborted)) key = "status-draft-aborted";
                else if (RunEntries.Is(entry, RunKeys.Unranked)) key = "status-draft-unranked";
                else if (!RunEntries.Is(entry, RunKeys.VideoPath)) key = "status-draft-add";
                else if (videoUpload && !RunEntries.Is(entry, RunKeys.UploadGivenUp) && !RunEntries.Is(entry, RunKeys.Untrimmed)) key = "status-draft-auto-upload";
                else key = "status-draft-upload";
                var label = Tr(language, key);
                var note = EntryNote(entry, language);
                return note is null ? label : $"{label} · {note}";
            case RunStatus.Duplicate:
                return Tr(language, "status-duplicate");
            case RunStatus.Rejected:
                return Tr(language, "status-rejected", ReasonText(entry));
            case RunStatus.Failed:
                return Tr(language, "status-failed", ReasonText(entry));
            default:
                return "?";
        }
    }

    /// <summary>
    /// run-video-label: the Video column - the recording's journey from disk to the site.
    /// </summary>
    /// <param name="entry">The queue entry.</param>
    /// <param name="language">UI language.</param>
    /// <param name="uploadPercent"><see cref="RunQueue.UploadProgressPercent"/> for this entry.</param>
    public static string RunVideoLabel(Plist entry, Language language, int? uploadPercent)
    {
        if (RunEntries.Is(entry, RunKeys.VideoUploaded)) return Tr(language, "video-uploaded");
        if (RunEntries.Is(entry, RunKeys.VideoAttached)) return Tr(language, "video-attached");
        if (uploadPercent is { } percent) return Tr(language, "video-uploading", percent);
        if (RunEntries.Is(entry, RunKeys.UploadGivenUp)) return Tr(language, "video-upload-failed");
        if (RunEntries.Is(entry, RunKeys.VideoPath)) return Tr(language, "video-saved");
        return "";
    }

    private static SexpNode Val(Plist entry, string key) => RunEntries.Get(entry, key);

    private static string ReasonText(Plist entry) =>
        Val(entry, RunKeys.Reason) is { IsTrue: true } reason ? Aesthetic(reason) : "?";

    // (or quest-name quest-slug) printed with ~a.
    private static string QuestLabel(Plist entry) =>
        Aesthetic(Val(entry, RunKeys.QuestName) is { IsTrue: true } name ? name : Val(entry, RunKeys.QuestSlug));

    // FORMAT ~a: strings raw, everything else as printed.
    private static string Aesthetic(SexpNode node) => node switch
    {
        SString s => s.Value,
        _ when node.IsNil => "NIL",
        _ => SexpWriter.Write(node),
    };

    private static long FloorDiv(long a, long b) => (long)Math.Floor((decimal)a / b);
}

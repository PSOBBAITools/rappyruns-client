using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Config;

/// <summary>
/// The config.sexp keys (keyword names without the colon, upper case as the
/// Lisp printer writes them) and their defaults - a port of
/// <c>*default-config*</c> (config.lisp:13, spec core §3.2). The file is
/// shared with the Lisp client, so these names are a contract.
/// </summary>
public static class ConfigKeys
{
    public const string ServerUrl = "SERVER-URL";
    public const string ApiToken = "API-TOKEN";
    public const string AnonToken = "ANON-TOKEN";
    public const string Language = "LANGUAGE";
    public const string AutoSubmit = "AUTO-SUBMIT";
    public const string SubmitAborted = "SUBMIT-ABORTED";
    public const string AutoUpdate = "AUTO-UPDATE";
    public const string CompletionSound = "COMPLETION-SOUND";
    public const string TriggerLog = "TRIGGER-LOG";
    public const string RecordEnabled = "RECORD-ENABLED";
    public const string TrackingOnly = "TRACKING-ONLY";
    public const string TrackingPrivate = "TRACKING-PRIVATE";
    public const string RecordAudio = "RECORD-AUDIO";
    public const string VideoUpload = "VIDEO-UPLOAD";
    public const string RecordMaxTotalGb = "RECORD-MAX-TOTAL-GB";
    public const string AutoPublish = "AUTO-PUBLISH";
    public const string HwEncode = "HW-ENCODE";
    public const string FfmpegPath = "FFMPEG-PATH";
    public const string RecordDir = "RECORD-DIR";
    public const string Moderator = "MODERATOR";
    public const string CloseToTray = "CLOSE-TO-TRAY";
    public const string RankToast = "RANK-TOAST";
    public const string GhostRace = "GHOST-RACE";
    public const string GhostOverlay = "GHOST-OVERLAY";
    public const string GhostMarker = "GHOST-MARKER";
    public const string OverlayCorner = "OVERLAY-CORNER";
    public const string OverlayPosition = "OVERLAY-POSITION";
    public const string PinshareAllowed = "PINSHARE-ALLOWED";
    public const string PinshareEnabled = "PINSHARE-ENABLED";
    public const string PinshareChannel = "PINSHARE-CHANNEL";
    public const string PinshareServer = "PINSHARE-SERVER";
    public const string StartMinimized = "START-MINIMIZED";
    public const string Debug = "DEBUG";

    /// <summary>Hidden power-user key: owner/name of the releases repo to update from (updater.lisp:32).</summary>
    public const string UpdateRepo = "UPDATE-REPO";

    /// <summary>Hidden key read by the recorder (no default; absent = NIL).</summary>
    public const string WgcDisable = "WGC-DISABLE";

    /// <summary>Dropped key scrubbed by <see cref="ConfigStore.Migrate"/>.</summary>
    public const string TokenPromptShown = "TOKEN-PROMPT-SHOWN";

    /// <summary>The production server; the default <see cref="ServerUrl"/>.</summary>
    public const string DefaultServerUrl = "https://rappyruns-production.up.railway.app";

    /// <summary>
    /// <c>+forced-config-keys+</c> (config.lisp:9): no longer user-adjustable.
    /// Any saved value is removed on load so the default below always wins.
    /// </summary>
    public static IReadOnlyList<string> Forced { get; } =
        [AutoSubmit, SubmitAborted, CompletionSound, RecordEnabled, VideoUpload];

    /// <summary>The overlay anchors <see cref="OverlayCorner"/> may hold (ghost.lisp:274).</summary>
    public static IReadOnlyList<string> OverlayCorners { get; } =
        ["TOP-RIGHT", "TOP-LEFT", "BOTTOM-RIGHT", "BOTTOM-LEFT",
         "MIDDLE-RIGHT", "MIDDLE-LEFT", "TOP-CENTER", "BOTTOM-CENTER", "CUSTOM"];

    /// <summary>
    /// <c>*default-config*</c> in its Lisp order. Order matters: a first run
    /// writes exactly this list (minus the forced keys) to config.sexp, like
    /// the Lisp client does.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, SexpNode>> Defaults { get; } =
    [
        new(ServerUrl, SexpNode.Str(DefaultServerUrl)),
        new(ApiToken, SexpNode.Str("")),
        new(AnonToken, SexpNode.Str("")),
        new(Language, SexpNode.Kw("EN")),
        new(AutoSubmit, SexpNode.T),
        new(SubmitAborted, SexpNode.T),
        new(AutoUpdate, SexpNode.T),
        new(CompletionSound, SexpNode.Nil),
        new(TriggerLog, SexpNode.Nil),
        new(RecordEnabled, SexpNode.T),
        new(TrackingOnly, SexpNode.Nil),
        new(TrackingPrivate, SexpNode.Nil),
        new(RecordAudio, SexpNode.T),
        new(VideoUpload, SexpNode.T),
        new(RecordMaxTotalGb, SexpNode.Int(20)),
        new(AutoPublish, SexpNode.Nil),
        new(HwEncode, SexpNode.T),
        new(FfmpegPath, SexpNode.Str("")),
        new(RecordDir, SexpNode.Str("")),
        new(Moderator, SexpNode.Nil),
        new(CloseToTray, SexpNode.T),
        new(RankToast, SexpNode.T),
        new(GhostRace, SexpNode.T),
        new(GhostOverlay, SexpNode.Nil),
        new(GhostMarker, SexpNode.T),
        new(OverlayCorner, SexpNode.Kw("TOP-RIGHT")),
        new(OverlayPosition, SexpNode.Nil),
        new(PinshareAllowed, SexpNode.Nil),
        new(PinshareEnabled, SexpNode.Nil),
        new(PinshareChannel, SexpNode.Str("")),
        new(PinshareServer, SexpNode.Str("")),
        new(StartMinimized, SexpNode.Nil),
        new(Debug, SexpNode.Nil),
    ];

    private static readonly Dictionary<string, SexpNode> DefaultsByKey =
        Defaults.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>The default for <paramref name="key"/>; NIL for keys without one (getf's default).</summary>
    public static SexpNode DefaultFor(string key) =>
        DefaultsByKey.TryGetValue(key, out var value) ? value : SexpNode.Nil;

    /// <summary>A fresh plist holding every default, in order (<c>(copy-list *default-config*)</c>).</summary>
    public static Plist DefaultPlist()
    {
        var plist = new Plist();
        // Plist.Set pushes new keys to the front, so add in reverse.
        for (var i = Defaults.Count - 1; i >= 0; i--) plist.Set(Defaults[i].Key, Defaults[i].Value);
        return plist;
    }
}

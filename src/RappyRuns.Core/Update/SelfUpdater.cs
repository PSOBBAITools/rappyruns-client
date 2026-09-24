using RappyRuns.Core.Api;

namespace RappyRuns.Core.Update;

/// <summary>
/// Release discovery and download (updater.lisp <c>fetch-latest-release</c>,
/// <c>download-update!</c>; spec core §10.2, §10.4) plus the startup decision. The
/// UI choreography (splash, dialogs) stays in the app; the hand-over lives in
/// RappyRuns.Win.Update.UpdateLauncher.
/// </summary>
/// <param name="transport">Shared HTTP transport (sends the required User-Agent).</param>
/// <param name="configuredRepo">Reads config <c>:update-repo</c> at call time (null/"" = default).</param>
/// <param name="currentVersion">The running version; null for a dev build (never updates).</param>
/// <param name="installDir">The folder holding the running exe.</param>
public sealed class SelfUpdater(HttpTransport transport, Func<string?> configuredRepo, string? currentVersion, string installDir)
{
    private const long Megabyte = 1048576;

    /// <summary>The releases repository in effect (config override or the default).</summary>
    public string Repo => UpdateConstants.ResolveRepo(configuredRepo());

    /// <summary>The page to offer for a manual download.</summary>
    public string ReleasePageUrl => UpdateConstants.ReleasePageUrl(Repo);

    /// <summary>The running version (null = dev build).</summary>
    public string? CurrentVersion => currentVersion;

    /// <summary>The install folder.</summary>
    public string InstallDir => installDir;

    /// <summary>The busy-deferral state shared by the Settings button and the poll loop.</summary>
    public DeferredUpdate Deferred { get; } = new();

    /// <summary>Whether the install folder accepts writes right now.</summary>
    public bool InstallDirWritable() => UpdateFiles.IsDirWritable(installDir);

    /// <summary>True when <paramref name="release"/> is newer than the running build.</summary>
    public bool IsNewer(ReleaseInfo release) => ClientVersion.UpdateAvailable(currentVersion, release.Tag);

    /// <summary>
    /// <c>fetch-latest-release</c>: GET https://api.github.com/repos/{repo}/releases/latest
    /// (Accept: application/vnd.github+json, unauthenticated = 60 requests/hour). Any
    /// non-200 (404 before the first release, 403 when rate limited) or transport
    /// failure returns null: "no update today". Prereleases and drafts never appear here.
    /// </summary>
    public async Task<ReleaseInfo?> FetchLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var r = await transport.SendAsync("GET", UpdateConstants.LatestReleaseApiUrl(Repo),
                headers: [new("Accept", "application/vnd.github+json")],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return r.Status == 200 ? ReleaseInfo.Parse(r.Body) : null;
        }
        catch (ApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// The pre-window pass (<c>startup-auto-update</c> minus the UI): fetch, then
    /// <see cref="UpdatePolicy.StartupDecision"/>. Only <see cref="UpdateDecision.Apply"/>
    /// should download; the release is returned for it.
    /// </summary>
    /// <param name="rejectedTag">The tag the update helper rolled back recently (<see cref="UpdateFiles.RejectedTag"/>), or null.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    public async Task<(UpdateDecision Decision, ReleaseInfo? Release)> StartupCheckAsync(
        string? rejectedTag = null, CancellationToken cancellationToken = default)
    {
        var release = await FetchLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        return (UpdatePolicy.StartupDecision(release, currentVersion, InstallDirWritable(), rejectedTag), release);
    }

    /// <summary>
    /// <c>download-update!</c>: download the release zip to <paramref name="target"/>
    /// (default %TEMP%\RappyRunsClient-update.zip, resolved now) and verify size and PK
    /// magic. Returns the path, or null with no leftover file on any failure.
    /// <paramref name="onProgress"/> gets (bytes, Content-Length or null) per 64 KB chunk.
    /// </summary>
    public async Task<string?> DownloadAsync(ReleaseInfo release, string? target = null,
        Action<long, long?>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var path = target ?? UpdateFiles.ZipPath();
        try
        {
            var status = await transport.DownloadToFileAsync(release.AssetUrl, path, onProgress: onProgress,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (status == 200 && UpdateFiles.IsValidZip(path, release.AssetSize)) return path;
        }
        catch (Exception ex) when (ex is ApiException or IOException or UnauthorizedAccessException)
        {
            // Fall through: delete whatever arrived.
        }
        catch (OperationCanceledException)
        {
            UpdateFiles.TryDelete(path);
            throw;
        }
        UpdateFiles.TryDelete(path);
        return null;
    }

    /// <summary>
    /// <c>download-release-with-progress</c>'s throttle: wraps <paramref name="show"/> so it
    /// runs once per whole megabyte (the progress callback fires per 64 KB chunk and each
    /// status line is a UI message) with (MB so far, ceil(total / 1 MB) or null) — the
    /// arguments of <c>:update-downloading</c> after the tag.
    /// </summary>
    public static Action<long, long?> MegabyteProgress(Action<long, long?> show)
    {
        long lastShown = -1;
        return (done, total) =>
        {
            var mb = done / Megabyte;
            if (mb <= lastShown) return;
            lastShown = mb;
            show(mb, total is { } t ? (t + Megabyte - 1) / Megabyte : null);
        };
    }
}

/// <summary>
/// <c>*update-ready-zip*</c> + <c>*poll-busy-p*</c>: the exe is never swapped while a
/// quest run or a recording is in flight. A download finishing while busy is parked
/// here and handed back exactly once at the first idle poll frame
/// (<c>note-poll-activity</c>). Thread-safe (download worker vs poll thread).
/// </summary>
public sealed class DeferredUpdate
{
    private readonly Lock _lock = new();
    private bool _busy;
    private ReadyUpdate? _ready;

    /// <summary>A verified download waiting for idle.</summary>
    public sealed record ReadyUpdate(string ZipPath, string Tag);

    /// <summary>True while a run or recording is in flight (as of the last poll frame).</summary>
    public bool IsBusy
    {
        get
        {
            lock (_lock) return _busy;
        }
    }

    /// <summary>The parked update, if any (for display).</summary>
    public ReadyUpdate? Pending
    {
        get
        {
            lock (_lock) return _ready;
        }
    }

    /// <summary>
    /// After a verified download: true = idle, apply now; false = busy, parked
    /// (show <c>:update-after-run</c>) until <see cref="NoteActivity"/> hands it back.
    /// Either way this download replaces any update parked earlier: applied
    /// now, a stale parked one must not be handed back for a second hand-over.
    /// </summary>
    public bool OfferOrDefer(string zipPath, string tag)
    {
        lock (_lock)
        {
            if (!_busy)
            {
                _ready = null;
                return true;
            }
            _ready = new ReadyUpdate(zipPath, tag);
            return false;
        }
    }

    /// <summary>
    /// <c>note-poll-activity</c>, once per poll frame: records whether the detector is in
    /// a quest or the recorder is recording, and when idle returns the parked update
    /// (clearing it, so it is applied exactly once); null otherwise.
    /// </summary>
    public ReadyUpdate? NoteActivity(bool inQuest, bool recording)
    {
        lock (_lock)
        {
            _busy = inQuest || recording;
            if (_busy || _ready is null) return null;
            var ready = _ready;
            _ready = null;
            return ready;
        }
    }
}

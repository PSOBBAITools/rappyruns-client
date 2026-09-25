using RappyRuns.Core.I18n;

namespace RappyRuns.Core.Api;

/// <summary>How a browser pairing ended (<c>run-pairing-flow</c>).</summary>
public enum PairingOutcome
{
    /// <summary>Approved; the token is saved and <see cref="AuthService.TokenLinked"/> fired.</summary>
    Completed,

    /// <summary>The code expired (404) or the poll budget ran out → <c>:pairing-expired</c>.</summary>
    Expired,

    /// <summary>A token was pasted in Settings meanwhile; it wins, nothing to show.</summary>
    Superseded,

    /// <summary>Cancelled through the token (shutdown), nothing to show.</summary>
    Cancelled,

    /// <summary>POST /api/pair failed → <c>:pairing-failed</c> in red (the next launch retries).</summary>
    FailedToStart,

    /// <summary>Another pairing is still waiting for approval; this call did nothing.</summary>
    AlreadyRunning,
}

/// <summary>The end of a pairing; <see cref="Error"/> is the failure message for <see cref="PairingOutcome.FailedToStart"/>.</summary>
public sealed record PairingResult(PairingOutcome Outcome, string? Token = null, string? Error = null)
{
    /// <summary>The token-status line to show, or null when the outcome shows nothing new.</summary>
    public string? StatusText(Language language) => Outcome switch
    {
        PairingOutcome.Expired => Strings.Default.Tr(language, "pairing-expired"),
        PairingOutcome.FailedToStart => Strings.Default.Tr(language, "pairing-failed", Error),
        _ => null,
    };

    /// <summary>True when <see cref="StatusText"/> is an error (shown in red).</summary>
    public bool IsError => Outcome == PairingOutcome.FailedToStart;
}

/// <summary>How a login.txt login ended (<c>run-file-login-flow</c>).</summary>
public enum FileLoginOutcome
{
    /// <summary>login.txt unreadable or incomplete → <c>:file-login-bad-file</c> (red).</summary>
    BadFile,

    /// <summary>201: token saved and <see cref="AuthService.TokenLinked"/> fired.</summary>
    Ok,

    /// <summary>401 → <c>:file-login-invalid</c> (red).</summary>
    Invalid,

    /// <summary>Transport/unexpected status → <c>:file-login-failed</c> (red).</summary>
    Failed,
}

/// <summary>A login.txt login result.</summary>
public sealed record FileLoginResult(FileLoginOutcome Outcome, string? Error = null)
{
    /// <summary>The token-status line for failures; null on success (the token check takes over).</summary>
    public string? StatusText(Language language) => Outcome switch
    {
        FileLoginOutcome.BadFile => Strings.Default.Tr(language, "file-login-bad-file"),
        FileLoginOutcome.Invalid => Strings.Default.Tr(language, "file-login-invalid"),
        FileLoginOutcome.Failed => Strings.Default.Tr(language, "file-login-failed", Error),
        _ => null,
    };
}

/// <summary>The four ways a token check ends (<c>check-token</c>).</summary>
public enum TokenCheckKind
{
    /// <summary>No linked token: a supported state, no network call.</summary>
    Unlinked,

    /// <summary>GET /api/me 200.</summary>
    Ok,

    /// <summary>GET /api/me 401: the token is invalid or revoked.</summary>
    Unauthorized,

    /// <summary>The server could not be asked (the token itself may well be fine).</summary>
    Error,
}

/// <summary>
/// The outcome of <see cref="AuthService.CheckTokenAsync"/> with every effect the
/// app must apply, in the Lisp order (ui-shell §2.3): status text; Pin Share
/// permission; moderator role (rebuild the UI only when it changed); auto-publish
/// mirror; queue retry; optional dialog. Null flags mean "leave as is".
/// </summary>
public sealed record TokenCheckResult(TokenCheckKind Kind, MeUser? User = null, MergeResult? Merge = null,
    Exception? Error = null)
{
    /// <summary>The token-status line.</summary>
    public string StatusText(Language language) => Kind switch
    {
        TokenCheckKind.Unlinked => Strings.Default.Tr(language, "token-unlinked"),
        TokenCheckKind.Ok => Strings.Default.Tr(language, "token-ok", User?.Username ?? ""),
        TokenCheckKind.Unauthorized => Strings.Default.Tr(language, "token-invalid"),
        _ => ErrorText.TokenStatus(language, Error ?? new ApiException("unknown error")),
    };

    /// <summary>True when the status line is red.</summary>
    public bool IsError => Kind is TokenCheckKind.Unauthorized or TokenCheckKind.Error;

    /// <summary>
    /// The Pin Share rollout verdict to store (<c>set-pinshare-permission</c>): false when
    /// unlinked or unauthorized, the features flag on success, null (keep) on errors.
    /// </summary>
    public bool? PinShareAllowed => Kind switch
    {
        TokenCheckKind.Ok => User!.PinShareAllowed,
        TokenCheckKind.Error => null,
        _ => false,
    };

    /// <summary>The moderator flag to store on success (<c>apply-moderator-role</c>); null otherwise.</summary>
    public bool? IsModerator => Kind == TokenCheckKind.Ok ? User!.IsModerator : null;

    /// <summary>The server's auto-publish flag to mirror on success (<c>apply-auto-publish</c>); null otherwise.</summary>
    public bool? AutoPublish => Kind == TokenCheckKind.Ok ? User!.AutoPublish : null;

    /// <summary>A verified token flushes the local backlog (<c>*retry-requested*</c>).</summary>
    public bool RetryQueue => Kind == TokenCheckKind.Ok;

    /// <summary>
    /// True when <c>on-invalid</c> should run: a definite 401 (the app re-logs in with
    /// login.txt when present).
    /// </summary>
    public bool ShouldRelogin => Kind == TokenCheckKind.Unauthorized;

    /// <summary>The dialog for the Save-settings flow (<c>notify</c>), or null.</summary>
    public string? DialogText(Language language) => Kind switch
    {
        TokenCheckKind.Ok => Strings.Default.Tr(language, "token-ok-dialog", User?.Username ?? ""),
        TokenCheckKind.Unauthorized => Strings.Default.Tr(language, "token-rejected-dialog"),
        _ => null,
    };
}

/// <summary>
/// Account linking (spec core §8, ui-shell §2): browser pairing, login.txt login,
/// the anonymous guest, and the token check with the guest merge. UI-agnostic: the
/// app shows the returned status texts and applies the returned flags. All methods
/// are safe to call from any thread; run them off the UI thread.
/// </summary>
public sealed class AuthService
{
    private readonly ApiClient _api;
    private readonly IAuthSettings _settings;
    private readonly string? _machineName;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private int _pairing;

    /// <param name="api">The API client.</param>
    /// <param name="settings">Server URL and token storage (config).</param>
    /// <param name="machineName">
    /// Computer name for token labels; defaults to <see cref="Environment.MachineName"/>
    /// (Lisp <c>machine-instance</c>).
    /// </param>
    /// <param name="delay">The pairing poll wait (tests pass a no-op); defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public AuthService(ApiClient api, IAuthSettings settings, string? machineName = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _api = api;
        _settings = settings;
        _machineName = machineName ?? Store.Submission.SafeMachineName();
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// Raised (on the calling worker thread) after a token from pairing or login.txt
    /// was saved (<c>finish-pairing</c>). The app writes it into the token field of the
    /// live window and runs <see cref="CheckTokenAsync"/>.
    /// </summary>
    public event Action<string>? TokenLinked;

    /// <summary>True while a browser pairing waits for approval (one at a time).</summary>
    public bool IsPairing => Volatile.Read(ref _pairing) != 0;

    /// <summary>The token submissions go out with right now ("" when none).</summary>
    public string SubmissionToken => Tokens.Submission(_settings.ApiToken, _settings.AnonToken);

    /// <summary>No linked account (a guest does not count).</summary>
    public bool IsUnlinked => Tokens.IsUnlinked(_settings.ApiToken);

    /// <summary><c>pairing-label</c>: "Desktop client (&lt;machine&gt;)", or "Desktop client" without a name.</summary>
    public static string PairingLabel(string? machineName) =>
        string.IsNullOrEmpty(machineName) ? "Desktop client" : $"Desktop client ({machineName})";

    /// <summary><c>file-login-label</c>: the pairing label + " [login.txt]".</summary>
    public static string FileLoginLabel(string? machineName) => PairingLabel(machineName) + " [login.txt]";

    // ensure-submission-token and anonymous-client-label live with the queue
    // (RunQueue.EnsureSubmissionTokenAsync, Submission.AnonymousClientLabel),
    // the only caller.

    /// <summary>
    /// <c>finish-pairing</c>: save a token that arrived over the pairing or password API
    /// and raise <see cref="TokenLinked"/>.
    /// </summary>
    public void FinishPairing(string token)
    {
        _settings.ApiToken = token;
        _settings.Save();
        TokenLinked?.Invoke(token);
    }

    /// <summary>
    /// <c>run-pairing-flow</c> (spec core §8.2): register a code, have
    /// <paramref name="openBrowser"/> open <c>&lt;server&gt;/pair?code=...</c>, call
    /// <paramref name="onWaiting"/> (show <c>:pairing-waiting</c>), then poll every
    /// <c>interval</c> seconds up to ceil(expires_in / interval) times. Transport errors
    /// while polling count as pending (Wi-Fi blinks do not abort); a token pasted in
    /// Settings meanwhile wins; cancellation ends quietly. Only one pairing runs at a time.
    /// </summary>
    public async Task<PairingResult> RunPairingAsync(Action<string> openBrowser, Action? onWaiting = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _pairing, 1, 0) != 0) return new PairingResult(PairingOutcome.AlreadyRunning);
        try
        {
            PairingStart start;
            try
            {
                start = await _api.StartPairingAsync(PairingLabel(_machineName), cancellationToken).ConfigureAwait(false);
            }
            catch (ApiException ex)
            {
                return new PairingResult(PairingOutcome.FailedToStart, Error: ex.Message);
            }
            openBrowser(Urls.PairingUrl(_settings.ServerUrl, start.Code));
            onWaiting?.Invoke();
            // interval <= 0 would divide by zero in the Lisp; fall back to the default 2 s.
            var interval = start.IntervalSeconds > 0 ? start.IntervalSeconds : 2;
            var attempts = (long)Math.Ceiling(start.ExpiresInSeconds / interval);
            for (long i = 0; i < attempts; i++)
            {
                await _delay(TimeSpan.FromSeconds(interval), cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) return new PairingResult(PairingOutcome.Cancelled);
                if (!Tokens.IsUnlinked(_settings.ApiToken)) return new PairingResult(PairingOutcome.Superseded);
                PairingPoll poll;
                try
                {
                    poll = await _api.PollPairingAsync(start.Code, cancellationToken).ConfigureAwait(false);
                }
                catch (ApiException)
                {
                    poll = new PairingPoll(PairingPollStatus.Pending);
                }
                switch (poll.Status)
                {
                    case PairingPollStatus.Gone:
                        return new PairingResult(PairingOutcome.Expired);
                    case PairingPollStatus.Complete:
                        FinishPairing(poll.Token!);
                        return new PairingResult(PairingOutcome.Completed, poll.Token);
                }
            }
            return new PairingResult(PairingOutcome.Expired);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PairingResult(PairingOutcome.Cancelled);
        }
        finally
        {
            Volatile.Write(ref _pairing, 0);
        }
    }

    /// <summary>
    /// <c>run-file-login-flow</c> (spec core §8.3): exchange login.txt credentials
    /// (parsed by the config side; pass nulls when the file was unreadable or
    /// incomplete) for a token with label "Desktop client (&lt;machine&gt;) [login.txt]".
    /// The app shows <c>:file-login-checking</c> before calling. Never falls back to the
    /// browser pairing.
    /// </summary>
    public async Task<FileLoginResult> LoginWithCredentialsAsync(string? username, string? password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return new FileLoginResult(FileLoginOutcome.BadFile);
        try
        {
            var result = await _api.LoginAsync(username, password, FileLoginLabel(_machineName), cancellationToken)
                .ConfigureAwait(false);
            if (result.Status == LoginStatus.Unauthorized) return new FileLoginResult(FileLoginOutcome.Invalid);
            FinishPairing(result.Token!);
            return new FileLoginResult(FileLoginOutcome.Ok);
        }
        catch (ApiException ex)
        {
            return new FileLoginResult(FileLoginOutcome.Failed, ex.Message);
        }
    }


    /// <summary>
    /// <c>check-token</c> (spec core §8.5): verify the linked token against GET /api/me.
    /// On success, <paramref name="onVerified"/> runs first (apply the Pin Share,
    /// moderator and auto-publish flags before anything fallible). Unlinked returns at
    /// once without network. The app shows <c>:token-checking</c> before calling and
    /// applies the returned result. The Lisp merged a pending guest here; the C#
    /// app does it with <see cref="MergeGuestAsync"/> once it knows the check is
    /// still current (S48), so the result's <see cref="TokenCheckResult.Merge"/> is
    /// always null from here.
    /// </summary>
    public async Task<TokenCheckResult> CheckTokenAsync(Action<MeUser>? onVerified = null,
        CancellationToken cancellationToken = default)
    {
        var token = Tokens.Normalize(_settings.ApiToken);
        if (token.Length == 0) return new TokenCheckResult(TokenCheckKind.Unlinked);
        try
        {
            var me = await _api.FetchMeAsync(token, cancellationToken).ConfigureAwait(false);
            if (me.Unauthorized) return new TokenCheckResult(TokenCheckKind.Unauthorized);
            var user = me.User!;
            onVerified?.Invoke(user);
            return new TokenCheckResult(TokenCheckKind.Ok, user);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new TokenCheckResult(TokenCheckKind.Error, Error: ex);
        }
    }

    /// <summary>
    /// The guest merge of <c>check-token</c> (spec core §8.5): move a pending
    /// anonymous guest's runs into the account of <paramref name="token"/>, a
    /// token a check just verified. Ok or gone clears <c>:anon-token</c> (saved),
    /// but only while it is still the guest that was merged (a guest registered
    /// meanwhile is kept); an API error keeps it for the next check. Null when
    /// there was no guest or the merge failed. Anything but an API error (a
    /// config write failure, a bug) is thrown for the caller to report.
    /// </summary>
    public async Task<MergeResult?> MergeGuestAsync(string token, CancellationToken cancellationToken = default)
    {
        var anon = Tokens.Normalize(_settings.AnonToken);
        if (anon.Length == 0) return null;
        MergeResult merge;
        try
        {
            merge = await _api.MergeAnonymousAsync(anon, token, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            // Transport failure: keep the guest token, the next verification retries.
            return null;
        }
        if (Tokens.Normalize(_settings.AnonToken) == anon)
        {
            _settings.AnonToken = "";
            _settings.Save();
        }
        return merge;
    }

    /// <summary>
    /// One round of <c>pinshare-permission-loop</c> (the app calls it every 1800 s):
    /// the Pin Share verdict for the linked token — true/false on 200, false on 401 —
    /// or null to keep the cached verdict (unlinked, transport error, or the token
    /// changed while the request was out, making the answer stale). Never touches the
    /// moderator role.
    /// </summary>
    public async Task<bool?> RefreshPinShareAllowedAsync(CancellationToken cancellationToken = default)
    {
        var token = Tokens.Normalize(_settings.ApiToken);
        if (token.Length == 0) return null;
        try
        {
            var me = await _api.FetchMeAsync(token, cancellationToken).ConfigureAwait(false);
            if (token != Tokens.Normalize(_settings.ApiToken)) return null;
            return !me.Unauthorized && me.User!.PinShareAllowed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}

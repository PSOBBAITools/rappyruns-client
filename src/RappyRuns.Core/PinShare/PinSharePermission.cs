using System.Text.Json;

namespace RappyRuns.Core.PinShare;

/// <summary>How a <c>GET /api/me</c> call ended, as the permission check needs it.</summary>
public enum MeOutcome
{
    /// <summary>200 with the user object.</summary>
    Ok,
    /// <summary>401: the token is invalid or revoked.</summary>
    Unauthorized,
    /// <summary>Anything else that is not a transport exception (keeps the verdict).</summary>
    Other,
}

/// <summary>The result of the injected <c>/api/me</c> call.</summary>
public readonly record struct MeResult(MeOutcome Outcome, JsonElement? User);

/// <summary>
/// The staged-rollout verdict: whether the linked account may use Pin Share
/// (<c>/api/me</c> features, server env <c>ETA_PINSHARE_USERS</c>). Lisp
/// <c>*pinshare-allowed-p*</c> + <c>set-pinshare-permission</c>
/// (pinshare.lisp:489-507) + <c>pinshare-permission-loop</c>
/// (pinshare-win32.lisp:539). Seeded from the cached config
/// <c>:pinshare-allowed</c> so the Settings group is there on the first
/// frame. Touches no window: the relay obeys the flag within a tick and the
/// UI shows or hides the group when it sees <see cref="Changed"/>.
/// Thread-safe.
/// </summary>
public sealed class PinSharePermission
{
    /// <summary>How often a linked client re-asks: it is resident for days, and the token check only runs at startup and on Save.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1800);

    private readonly Action<bool>? _persist;
    private readonly object _lock = new();
    private bool _allowed;

    /// <param name="initial">The cached <c>:pinshare-allowed</c>.</param>
    /// <param name="persist">Stores the new verdict as config <c>:pinshare-allowed</c> (and saves the config); called on a change only.</param>
    public PinSharePermission(bool initial, Action<bool>? persist = null)
    {
        _allowed = initial;
        _persist = persist;
    }

    /// <summary>Raised (on the caller's thread) after the verdict changed.</summary>
    public event Action<bool>? Changed;

    public bool Allowed
    {
        get { lock (_lock) return _allowed; }
    }

    /// <summary>
    /// Records a verdict; true when it changed. The token check calls this
    /// with <see cref="PinShareSettings.FeatureAllowed"/> on success, false on
    /// 401 or when the token is emptied.
    /// </summary>
    public bool Set(bool allowed)
    {
        lock (_lock)
        {
            if (_allowed == allowed) return false;
            _allowed = allowed;
        }
        _persist?.Invoke(allowed);
        Changed?.Invoke(allowed);
        return true;
    }

    /// <summary>
    /// Re-asks <c>/api/me</c> every <paramref name="interval"/> (default
    /// 1800 s) while a token is linked, so the server can widen the rollout -
    /// or pull the feature - without a client restart. A transport error
    /// (the delegate throws) keeps the current verdict: an unreachable site
    /// must not switch a working relay off. An answer that arrives after the
    /// token changed is dropped (the token check already stored the verdict
    /// for the new one). The moderator role is not this loop's business.
    /// </summary>
    /// <param name="token">The normalized linked token ("" when unlinked), read fresh each time.</param>
    /// <param name="fetchMe">GET /api/me with the given token.</param>
    public async Task RunRefreshLoopAsync(
        Func<string> token,
        Func<string, CancellationToken, Task<MeResult>> fetchMe,
        CancellationToken cancellationToken,
        TimeSpan? interval = null)
    {
        var wait = interval ?? RefreshInterval;
        while (true)
        {
            try
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await RefreshOnceAsync(token, fetchMe, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One round of <see cref="RunRefreshLoopAsync"/>.</summary>
    public async Task RefreshOnceAsync(Func<string> token, Func<string, CancellationToken, Task<MeResult>> fetchMe, CancellationToken cancellationToken)
    {
        var asked = token();
        if (asked == "") return;
        MeResult result;
        try
        {
            result = await fetchMe(asked, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (asked != token()) return;
        switch (result.Outcome)
        {
            case MeOutcome.Ok:
                Set(PinShareSettings.FeatureAllowed(result.User));
                break;
            case MeOutcome.Unauthorized:
                Set(false);
                break;
        }
    }
}

using RappyRuns.Core.Api;

namespace RappyRuns.Host;

/// <summary>
/// Orders the token checks, which finish in any order: only the latest check
/// may apply its result (status line, Pin Share verdict, moderator role,
/// auto-publish, queue retry, login.txt relogin), and only while the token it
/// checked is still the configured one. A slow 401 for an old token must not
/// switch Pin Share off or re-login over a token paired meanwhile.
/// </summary>
internal sealed class TokenCheckGate
{
    private long _latest;

    /// <summary>One check: the token it verifies (normalized) and its place in the order.</summary>
    internal sealed class Ticket(TokenCheckGate gate, long sequence, string token)
    {
        public string Token { get; } = token;

        /// <summary>True while no later check started and <paramref name="configuredToken"/> is still the checked one.</summary>
        public bool IsCurrent(string? configuredToken) =>
            Volatile.Read(ref gate._latest) == sequence && ChecksConfigured(configuredToken);

        /// <summary>
        /// True while <paramref name="configuredToken"/> is still the checked one,
        /// even if a later check of it started: what the guest merge needs (S48).
        /// Merging into the account the player still uses is right whichever
        /// check does it, and waiting for the later one would lose the merge
        /// when that one fails on the network.
        /// </summary>
        public bool ChecksConfigured(string? configuredToken) => Tokens.Normalize(configuredToken) == Token;
    }

    /// <summary>Starts a check of <paramref name="configuredToken"/>; every earlier ticket goes stale.</summary>
    public Ticket Begin(string? configuredToken) =>
        new(this, Interlocked.Increment(ref _latest), Tokens.Normalize(configuredToken));

    /// <summary>
    /// A ticket for another writer of the token line (S49: the login.txt login,
    /// the browser pairing) that does not itself start a check: it stays
    /// current until a check starts after it or the token changes, so a
    /// late failure of that flow cannot overwrite a newer check's result.
    /// Earlier tickets stay as they were.
    /// </summary>
    public Ticket Watch(string? configuredToken) =>
        new(this, Volatile.Read(ref _latest), Tokens.Normalize(configuredToken));
}

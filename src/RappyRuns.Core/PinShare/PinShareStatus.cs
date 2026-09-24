using RappyRuns.Core.I18n;

namespace RappyRuns.Core.PinShare;

/// <summary>What the relay is doing, for the Settings status line (Lisp <c>*pinshare-status*</c> keywords).</summary>
public enum PinShareStatusKind
{
    Off,
    NotAllowed,
    NoChannel,
    WaitingGame,
    Connecting,
    /// <summary>Text = channel, Count = members in the channel.</summary>
    Connected,
    /// <summary>Connected, yet the game has not loaded the script (fresh install: it needs a Reload).</summary>
    ConnectedNoAddon,
    /// <summary>No passphrase, but a pin set to draw: in.txt without a server. Text = set name.</summary>
    LocalOnly,
    /// <summary>Text = the error.</summary>
    Error,
    NoAddonPlugin,
    /// <summary>Text = the reason.</summary>
    InstallFailed,
    /// <summary>Text = the addon folder path.</summary>
    BrokenLink,
    /// <summary>The old PowerShell relay still writes in.txt.</summary>
    Conflict,
}

/// <summary>
/// One relay status (Lisp <c>(keyword . args)</c>). Immutable, compared by
/// value: the relay thread swaps in a fresh one and the UI polls it.
/// </summary>
public sealed record PinShareStatus(PinShareStatusKind Kind, string? Text = null, int Count = 0)
{
    public static PinShareStatus Off { get; } = new(PinShareStatusKind.Off);

    /// <summary>
    /// (text, isError) for the Settings status line (Lisp
    /// <c>pinshare-status-text</c>, pinshare.lisp:463).
    /// </summary>
    public (string Text, bool IsError) Describe(Language language)
    {
        string Tr(string key, params object?[] args) => Strings.Default.Tr(language, key, args);
        return Kind switch
        {
            PinShareStatusKind.Off => (Tr("pinshare-status-off"), false),
            PinShareStatusKind.NotAllowed => (Tr("pinshare-status-not-allowed"), false),
            PinShareStatusKind.NoChannel => (Tr("pinshare-status-no-channel"), false),
            PinShareStatusKind.WaitingGame => (Tr("pinshare-status-waiting-game"), false),
            PinShareStatusKind.Connecting => (Tr("pinshare-status-connecting"), false),
            PinShareStatusKind.Connected => (Tr("pinshare-status-connected", Text, Count), false),
            PinShareStatusKind.ConnectedNoAddon => (Tr("pinshare-status-no-addon"), false),
            PinShareStatusKind.LocalOnly => (Tr("pinshare-status-local-only", Text), false),
            PinShareStatusKind.Error => (Tr("pinshare-status-error", Text), true),
            PinShareStatusKind.NoAddonPlugin => (Tr("pinshare-status-no-plugin"), true),
            PinShareStatusKind.InstallFailed => (Tr("pinshare-status-install-failed", Text), true),
            PinShareStatusKind.BrokenLink => (Tr("pinshare-status-broken-link", Text), true),
            PinShareStatusKind.Conflict => (Tr("pinshare-status-conflict"), true),
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null),
        };
    }
}

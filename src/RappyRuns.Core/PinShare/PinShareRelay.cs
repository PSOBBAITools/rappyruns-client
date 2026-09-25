using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RappyRuns.Core.PinShare;

/// <summary>One in.txt <c>alert</c> line (C#, S47): <c>alert\t&lt;code&gt;\t&lt;message&gt;[\t&lt;arg&gt;]</c>.</summary>
public sealed record RelayAlert(string Code, string Message, string? Arg = null);

/// <summary>
/// The relay's state for one session (Lisp <c>pinshare-relay</c> struct,
/// pinshare.lisp:29): the client stands in for the Pin Share addon's
/// network half. The addon (a Lua script inside the game) cannot open
/// sockets, so it talks to the relay through exchange\out.txt and
/// exchange\in.txt, and the relay talks WebSocket JSON to the relay
/// server. The server owns the pin list (numbering, expiry, per-owner
/// limits); this is a translator plus reconnect bookkeeping.
/// Not thread-safe: one relay session thread owns it.
/// </summary>
public sealed partial class PinShareRelay
{
    /// <summary>
    /// ADDON_VERSION of the init.lua this client ships (C#, S21). Kept in step
    /// with client/data/pin-share/init.lua by a test; bump both together.
    /// </summary>
    public const int BundledAddonVersion = 2;

    /// <summary>The party's shared passphrase ("" = local-only: a pin set drawn without a server).</summary>
    public string Channel { get; set; } = "";

    /// <summary>Fresh per relay start; the addon re-sends its name and colors when it sees a new one.</summary>
    public string Session { get; set; } = "";

    /// <summary>Highest out.txt seq handled (in.txt's "ack").</summary>
    public long LastSeq { get; set; }

    /// <summary>The player's name, as told by the addon.</summary>
    public string Name { get; set; } = "";

    /// <summary>Own pin color RRGGBB; "" = not told yet.</summary>
    public string Color { get; set; } = "";

    /// <summary>Own arrow color; "" = same as pins, null = not told yet.</summary>
    public string? ArrowColor { get; set; }

    /// <summary>The server's last state snapshot.</summary>
    public IReadOnlyList<JsonElement> Pins { get; private set; } = [];

    public IReadOnlyList<JsonElement> Arrows { get; private set; } = [];

    public IReadOnlyList<JsonElement> Members { get; private set; } = [];

    /// <summary>"connecting" / "connected" / "error" / "local" - the addon keys its status line off these exact strings.</summary>
    public string Status { get; private set; } = "connecting";

    public string StatusMessage { get; private set; } = "";

    /// <summary>
    /// The addon has written a command to THIS session: proof the running
    /// game loaded the script (a fresh install needs Reload in the game's
    /// addon menu).
    /// </summary>
    public bool AddonSeen { get; set; }

    /// <summary>The <c>version</c> the addon sent to THIS session, or null (none yet, or an addon older than versions).</summary>
    public int? AddonVersion { get; private set; }

    /// <summary>The addon sent its name to THIS session (the backlog's does not count).</summary>
    public bool NameSeen { get; private set; }

    /// <summary>
    /// The game runs an older addon than the one installed next to it
    /// (<see cref="InstalledAddonVersion"/>; an
    /// update landed while the game kept the old script loaded). Judged once
    /// the addon's name has arrived: a current addon sends its version first,
    /// so a name with no version is an addon from before versions.
    /// </summary>
    public bool AddonOutdated => NameSeen && (AddonVersion ?? 0) < InstalledAddonVersion;

    /// <summary>in.txt <c>alert</c> code: the running addon is older than the installed one.</summary>
    public const string AlertAddonOutdated = "addon_outdated";

    /// <summary>The English fallback for <see cref="AlertAddonOutdated"/> (the addon from version 2 words it itself).</summary>
    public const string AlertAddonOutdatedMessage = "This addon is outdated: Reload it from the game's addon menu";

    /// <summary>
    /// What the client wants the addon's window to say about the client's
    /// view of it (C#, S47), one in.txt <c>alert</c> line each; empty for
    /// none. <see cref="RelayAlert.Code"/> is a stable token the addon may
    /// word itself; <see cref="RelayAlert.Message"/> is the English fallback
    /// it shows for a code it does not know, so a later client can add codes
    /// without an addon update. <see cref="AlertAddonOutdated"/> is
    /// conditional: its argument is the installed version, and only an addon
    /// older than that shows it. in.txt is shared by every game window on the
    /// install and <see cref="AddonOutdated"/> reflects whichever addon sent
    /// its name last, so the line goes out whenever an addon has spoken to
    /// this session and a version is installed, and each window judges
    /// itself. Every input changes only in <see cref="Consume"/> /
    /// <see cref="SkipBacklog"/> or per session, which already mark in.txt dirty.
    /// </summary>
    public IReadOnlyList<RelayAlert> Alerts =>
        NameSeen && InstalledAddonVersion > 0
            ? [new RelayAlert(AlertAddonOutdated, AlertAddonOutdatedMessage, InstalledAddonVersion.ToString(CultureInfo.InvariantCulture))]
            : [];

    /// <summary>
    /// ADDON_VERSION of the init.lua installed next to the game - what a
    /// Reload loads, so the only fair yardstick (a developer's linked working
    /// copy is never overwritten with the bundled one). 0 when it has none,
    /// which never reads as outdated. The supervisor sets it per session.
    /// </summary>
    public int InstalledAddonVersion { get; set; } = BundledAddonVersion;

    /// <summary>The <c>local ADDON_VERSION = &lt;n&gt;</c> line of an init.lua, or 0 when it has none (C#, S21).</summary>
    public static int AddonVersionOf(string? initLua) =>
        initLua is not null && AddonVersionLine().Match(initLua) is { Success: true } match
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : 0;

    [GeneratedRegex(@"^local ADDON_VERSION = (\d{1,9})\s*$", RegexOptions.Multiline)]
    private static partial Regex AddonVersionLine();

    /// <summary>in.txt needs rewriting.</summary>
    public bool Dirty { get; set; } = true;

    /// <summary>
    /// The messages that (re)introduce this player to the server: hello,
    /// then the colors - the server keeps colors per name, so a new name
    /// needs them again (pinshare.lisp:210).
    /// </summary>
    public List<string> HelloMessages()
    {
        var messages = new List<string>
        {
            PinShareJson.Message(("t", "hello"), ("channel", Channel), ("name", Name)),
        };
        if (Color != "") messages.Add(PinShareJson.Message(("t", "color"), ("color", Color)));
        if (ArrowColor is not null) messages.Add(PinShareJson.Message(("t", "arrow_color"), ("color", ArrowColor)));
        return messages;
    }

    /// <summary>
    /// Handles the out.txt <paramref name="lines"/> newer than
    /// <see cref="LastSeq"/>; returns the JSON messages to send, oldest first
    /// (pinshare.lisp:224). While not <paramref name="connected"/> the
    /// identity commands still update the relay (the next hello replays
    /// them) and everything else is dropped on purpose: a pin placed minutes
    /// ago must not pop up after a reconnect. Moves and removes of pin-set
    /// items (negative ids) never reach the server.
    /// </summary>
    public List<string> Consume(IEnumerable<OutboxLine> lines, bool connected)
    {
        var messages = new List<string>();
        foreach (var line in lines)
        {
            if (line.Seq <= LastSeq) continue;
            LastSeq = line.Seq;
            AddonSeen = true;
            Dirty = true;
            var fields = line.Fields;
            var value = fields.Count > 2 ? PinShareText.Clean(fields[2]) : null;
            switch (line.Command)
            {
                case "version":
                    // version <n>: sent once per session, before the name. Old
                    // relays drop it as an unknown command.
                    if (value is not null && PinShareText.ParseInteger(value) is { } version)
                        AddonVersion = (int)Math.Clamp(version, 0, int.MaxValue);
                    break;
                case "name":
                    if (value is null) break;
                    Name = value;
                    NameSeen = true;
                    if (connected) messages.AddRange(HelloMessages());
                    break;
                case "color":
                    if (value is null) break;
                    Color = value;
                    if (connected) messages.Add(PinShareJson.Message(("t", "color"), ("color", value)));
                    break;
                case "arrow_color":
                    if (value is null) break;
                    ArrowColor = value;
                    if (connected) messages.Add(PinShareJson.Message(("t", "arrow_color"), ("color", value)));
                    break;
                default:
                    // A pin-set item is drawn from the site's copy, not the
                    // server's list: nothing to move or remove there.
                    if (Commands.IsLocalItemCommand(fields)) break;
                    if (connected && Commands.Message(fields) is { } message) messages.Add(message);
                    break;
            }
        }
        return messages;
    }

    /// <summary>
    /// Relay start: whatever already sits in out.txt is stale, so only the
    /// identity commands are kept and the seq cursor jumps past everything.
    /// Left over from before this session, it says nothing about whether the
    /// addon is alive now (pinshare.lisp:284).
    /// </summary>
    public PinShareRelay SkipBacklog(IEnumerable<OutboxLine> lines)
    {
        Consume(lines, connected: false);
        AddonSeen = false;
        // The version check is about the addon talking to this session.
        AddonVersion = null;
        NameSeen = false;
        return this;
    }

    /// <summary>
    /// Applies one server message (pinshare.lisp:295). A state snapshot
    /// replaces the pin, arrow and member lists (a missing key - a pre-arrow
    /// server - is an empty list). Returns the cleaned message of a
    /// server-side error for the log ("" when it has none, which still
    /// counts as an error), else null. Anything that is not a JSON object is
    /// ignored.
    /// </summary>
    public string? NoteMessage(string text)
    {
        if (PinShareJson.TryParse(text) is not { } parsed || JsonItem.From(parsed) is not { } message) return null;
        switch (PinShareJson.AsString(message.Get("t")))
        {
            case "state":
                Pins = PinShareJson.Items(message.Get("pins"));
                Arrows = PinShareJson.Items(message.Get("arrows"));
                Members = PinShareJson.Items(message.Get("members"));
                Dirty = true;
                return null;
            case "error":
                return PinShareText.Clean(PinShareJson.AsString(message.Get("message")));
            default:
                return null;
        }
    }

    /// <summary>Forget the server's lists (disconnect): the addon must not keep drawing pins nobody can confirm any more.</summary>
    public void ClearState()
    {
        Pins = [];
        Arrows = [];
        Members = [];
        Dirty = true;
    }

    /// <summary>Sets the in.txt status line; true when it actually changed (pinshare.lisp:324).</summary>
    public bool SetStatus(string status, string message)
    {
        if (status == Status && message == StatusMessage) return false;
        Status = status;
        StatusMessage = message;
        Dirty = true;
        return true;
    }
}

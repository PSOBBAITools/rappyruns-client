using System.Text;
using System.Text.Json;

namespace RappyRuns.Core.PinShare;

/// <summary>
/// exchange\in.txt, relay → addon: the whole pin/arrow list plus the
/// connection status, rewritten on every change and at least once a second
/// (the addon treats a "time" older than 5 s as "relay not running"). The
/// line order and columns are read by the addon's <c>readInbox</c>
/// (data/pin-share/init.lua), which checks the text ends in "\nend\n" to
/// reject a half-written file. Byte-exact port of
/// <c>render-pinshare-inbox</c> (pinshare.lisp:382).
/// </summary>
public static class Inbox
{
    private static readonly string[] PinNumbers = ["x", "y", "z"];
    private static readonly string[] PinTail = ["remaining", "label", "no", "ownerNo", "color", "roomNo", "room"];
    private static readonly string[] LocalPinTail = [.. PinTail, "locked"];
    private static readonly string[] ArrowNumbers = ["x1", "y1", "z1", "x2", "y2", "z2"];
    private static readonly string[] ArrowTail = ["remaining", "color"];
    private static readonly string[] ArrowExtra = ["xm", "ym", "zm", "room"];
    private static readonly string[] LocalArrowExtra = [.. ArrowExtra, "locked"];

    /// <summary>
    /// The full text of in.txt for <paramref name="relay"/>'s state at
    /// <paramref name="unixTime"/>, plus <paramref name="pinSet"/>'s items
    /// (the set chosen on the site for the loaded quest, or null) drawn as
    /// locked local pins, followed by a "pinset" line naming it.
    /// </summary>
    public static string Render(PinShareRelay relay, long unixTime, PinSet? pinSet = null)
    {
        var sb = new StringBuilder();
        void Line(params string[] fields) => sb.Append(PinShareText.Join(fields)).Append('\n');

        Line("session", relay.Session);
        Line("time", PinShareText.FormatNumber(unixTime));
        Line("ack", PinShareText.FormatNumber(relay.LastSeq));
        Line("status", relay.Status, PinShareText.Clean(relay.StatusMessage));
        // S47: only when there is one. Before an addon has sent its name to
        // this session (and for an unversioned install) there is none, so
        // in.txt is byte for byte the Lisp relay's; after it, addon_outdated
        // goes out for every window to judge. Addons before version 2 skip
        // line kinds they do not know.
        foreach (var alert in relay.Alerts)
        {
            var code = PinShareText.Clean(alert.Code);
            var message = PinShareText.Clean(alert.Message);
            if (alert.Arg is null) Line("alert", code, message);
            else Line("alert", code, message, PinShareText.Clean(alert.Arg));
        }
        Line("channel", PinShareText.Clean(relay.Channel));
        foreach (var member in relay.Members)
        {
            if (member.ValueKind == JsonValueKind.String) Line("member", PinShareText.Clean(member.GetString()));
        }
        foreach (var pin in relay.Pins)
        {
            if (JsonItem.From(pin) is { } item && ItemLine("pin", item, PinNumbers, PinTail) is { } text) Line(text);
        }
        foreach (var arrow in relay.Arrows)
        {
            if (JsonItem.From(arrow) is { } item && ItemLine("arrow", item, ArrowNumbers, ArrowTail, ArrowExtra) is { } text) Line(text);
        }
        if (pinSet is not null)
        {
            var (pins, arrows) = pinSet.LocalItems;
            foreach (var pin in pins)
            {
                if (ItemLine("pin", pin, PinNumbers, LocalPinTail) is { } text) Line(text);
            }
            foreach (var arrow in arrows)
            {
                if (ItemLine("arrow", arrow, ArrowNumbers, ArrowTail, LocalArrowExtra) is { } text) Line(text);
            }
            Line("pinset", pinSet.CleanName);
        }
        Line("end");
        return sb.ToString();
    }

    /// <summary>
    /// One "pin" / "arrow" line, or null when the id or floor is not an
    /// integer or a coordinate is not a number (the addon would drop the
    /// line anyway). <paramref name="extraKeys"/> is the arrow's trailing
    /// group - written only when its first value is a number, because the
    /// addon reads those columns by position (pinshare.lisp:359).
    /// </summary>
    private static string? ItemLine(string kind, JsonItem item, string[] numberKeys, string[] tailKeys, string[]? extraKeys = null)
    {
        var id = item.Get("id");
        var floor = item.Get("floor");
        if (!PinShareJson.IsInteger(id) || !PinShareJson.IsInteger(floor)) return null;
        var fields = new List<string>
        {
            kind,
            PinShareJson.FormatReal(id!.Value),
            PinShareText.Clean(PinShareJson.AsString(item.Get("owner"))),
            PinShareJson.FormatReal(floor!.Value),
        };
        foreach (var key in numberKeys)
        {
            var value = item.Get(key);
            if (!PinShareJson.IsReal(value)) return null;
            fields.Add(PinShareJson.FormatReal(value!.Value));
        }
        foreach (var key in tailKeys) fields.Add(PinShareJson.Field(item.Get(key)));
        if (extraKeys is not null && PinShareJson.IsReal(item.Get(extraKeys[0])))
        {
            foreach (var key in extraKeys) fields.Add(PinShareJson.Field(item.Get(key)));
        }
        return PinShareText.Join(fields);
    }

    /// <summary>
    /// The (session, unix time) another relay stamped into in.txt
    /// <paramref name="text"/>, nulls when absent (pinshare.lisp:432).
    /// </summary>
    public static (string? Session, long? Time) Heartbeat(string text)
    {
        string? session = null;
        long? time = null;
        foreach (var line in PinShareText.SplitOn('\n', text))
        {
            var fields = PinShareText.SplitOn('\t', line.TrimEnd('\r'));
            switch (fields[0])
            {
                case "session":
                    session = fields.Count > 1 ? fields[1] : null;
                    break;
                case "time":
                    time = fields.Count > 1 ? PinShareText.ParseInteger(fields[1]) : null;
                    break;
            }
        }
        return (session, time);
    }

    /// <summary>
    /// True when in.txt <paramref name="text"/> carries a heartbeat no older
    /// than 3 s from a relay that is not one of <paramref name="ownSessions"/>
    /// (every session id this process has used: a passphrase change restarts
    /// the relay within the old heartbeat's freshness window). Used once at
    /// relay start to notice the old PowerShell relay still running - two
    /// relays would fight over both files (pinshare.lisp:446).
    /// </summary>
    public static bool IsForeignRelay(string text, IEnumerable<string> ownSessions, long unixTime)
    {
        var (session, time) = Heartbeat(text);
        return session is not null && time is { } t
            && !ownSessions.Contains(session, StringComparer.Ordinal)
            && Math.Abs(unixTime - t) <= 3;
    }
}

namespace RappyRuns.Core.PinShare;

/// <summary>
/// Translation of out.txt commands into the relay server's JSON messages
/// (Lisp <c>pinshare-command-message</c>, pinshare.lisp:139).
/// </summary>
public static class Commands
{
    private static readonly string[] Xyz = ["x", "y", "z"];
    private static readonly string[] Ends = ["x1", "y1", "z1", "x2", "y2", "z2"];
    private static readonly string[] Middle = ["xm", "ym", "zm"];

    /// <summary>
    /// One command's <paramref name="fields"/> (seq, command, args...) → the
    /// JSON text to send, or null when the command is unknown or malformed
    /// (a coordinate the addon printed as "nan", a non-integral id...).
    /// name / color / arrow_color are not here: they change relay state too
    /// (<see cref="PinShareRelay.Consume"/>).
    /// </summary>
    public static string? Message(IReadOnlyList<string> fields)
    {
        if (fields.Count < 2) return null;
        var command = fields[1];
        var args = fields.Skip(2).ToList();
        string? Arg(int index) => index < args.Count ? args[index] : null;

        switch (command)
        {
            // add <floor> <x> <y> <z> <ttl> [<label> [<max> [<room>]]]
            case "add" when args.Count >= 5:
            {
                var floor = PinShareText.ParseInteger(args[0]);
                var point = Numbers(args, 1, Xyz);
                var ttl = PinShareText.ParseInteger(args[4]);
                if (floor is null || ttl is null || point is null) return null;
                var pairs = new List<(string, object)>
                {
                    ("t", "add"), ("floor", floor.Value), ("ttl", ttl.Value), ("label", Arg(5) ?? ""),
                };
                pairs.AddRange(point);
                AddOptionalInteger(pairs, Arg(6), "max");
                AddOptionalInteger(pairs, Arg(7), "room");
                return PinShareJson.Message([.. pairs]);
            }
            // move <id> <x> <y> <z>
            case "move" when args.Count >= 4:
            {
                var id = PinShareText.ParseInteger(args[0]);
                var point = Numbers(args, 1, Xyz);
                if (id is null || point is null) return null;
                return PinShareJson.Message([("t", "move"), ("id", id.Value), .. point]);
            }
            // arrow_add <floor> <x1> <y1> <z1> <x2> <y2> <z2> <ttl> <max> [<xm> <ym> <zm> [<room>]]
            case "arrow_add" when args.Count >= 9:
            {
                var floor = PinShareText.ParseInteger(args[0]);
                var ends = Numbers(args, 1, Ends);
                var ttl = PinShareText.ParseInteger(args[7]);
                var max = PinShareText.ParseInteger(args[8]);
                var middle = args.Count >= 12 ? Numbers(args, 9, Middle) : [];
                if (floor is null || ttl is null || max is null || ends is null || middle is null) return null;
                var pairs = new List<(string, object)>
                {
                    ("t", "arrow_add"), ("floor", floor.Value), ("ttl", ttl.Value), ("max", max.Value),
                };
                pairs.AddRange(ends);
                pairs.AddRange(middle);
                AddOptionalInteger(pairs, Arg(12), "room");
                return PinShareJson.Message([.. pairs]);
            }
            // arrow_move <id> <x1> <y1> <z1> <x2> <y2> <z2> [<xm> <ym> <zm>]
            case "arrow_move" when args.Count >= 7:
            {
                var id = PinShareText.ParseInteger(args[0]);
                var ends = Numbers(args, 1, Ends);
                var middle = args.Count >= 10 ? Numbers(args, 7, Middle) : [];
                if (id is null || ends is null || middle is null) return null;
                return PinShareJson.Message([("t", "arrow_move"), ("id", id.Value), .. ends, .. middle]);
            }
            case "remove" or "arrow_remove" when args.Count >= 1:
            {
                var id = PinShareText.ParseInteger(args[0]);
                return id is null ? null : PinShareJson.Message(("t", command), ("id", id.Value));
            }
            case "clear_mine" or "clear_all":
                return PinShareJson.Message(("t", command));
            default:
                return null;
        }
    }

    /// <summary>
    /// True for a move / remove of a negative id: the pin-set items the relay
    /// draws locally never reach the server (Lisp
    /// <c>pinshare-local-item-command-p</c>, pinshare.lisp:276).
    /// </summary>
    public static bool IsLocalItemCommand(IReadOnlyList<string> fields) =>
        fields.Count >= 3
        && fields[1] is "move" or "remove" or "arrow_move" or "arrow_remove"
        && PinShareText.ParseInteger(fields[2]) is < 0;

    // (key value ...) pairs, or null when any value fails to parse.
    private static List<(string, object)>? Numbers(List<string> args, int start, string[] keys)
    {
        var pairs = new List<(string, object)>(keys.Length);
        for (var i = 0; i < keys.Length; i++)
        {
            if (PinShareText.ParseNumber(args[start + i]) is not { } number) return null;
            pairs.Add((keys[i], number));
        }
        return pairs;
    }

    private static void AddOptionalInteger(List<(string, object)> pairs, string? text, string key)
    {
        if (text is not null && PinShareText.ParseInteger(text) is { } value) pairs.Add((key, value));
    }
}

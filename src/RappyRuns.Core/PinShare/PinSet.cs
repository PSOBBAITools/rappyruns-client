using System.Text.Json;

namespace RappyRuns.Core.PinShare;

/// <summary>
/// A pin set: a quest's pins and arrows saved on the site, the payload of
/// <c>GET /api/quests/:slug/pins</c> (the set the player chose there). The
/// relay draws it locally: its items join in.txt with negative ids - the
/// server's ids are positive, so they never collide - and a trailing locked
/// field the addon reads as "not yours to drag or delete". Nothing about
/// them goes to the relay server (pinshare.lisp:542).
/// </summary>
public sealed class PinSet
{
    /// <summary>The set name stands in for the owner on every local pin; cut so a long name does not bury the pin's own label (pinshare.lisp:552).</summary>
    public const int OwnerChars = 20;

    private readonly Lazy<(IReadOnlyList<JsonItem> Pins, IReadOnlyList<JsonItem> Arrows)> _localItems;

    public PinSet(JsonElement payload)
    {
        Payload = payload;
        Fields = JsonItem.From(payload) ?? JsonItem.Empty();
        _localItems = new(() => BuildLocalItems(Fields), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// The set from a fetch result, or null unless it is an object with an
    /// <c>items</c> object (the acceptance test of <c>maybe-start-pin-set-fetch</c>,
    /// pinshare-win32.lisp:515).
    /// </summary>
    public static PinSet? FromFetch(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } p) return null;
        var items = JsonItem.From(p)!.Get("items");
        return items is { ValueKind: JsonValueKind.Object } ? new PinSet(p) : null;
    }

    public JsonElement Payload { get; }

    public JsonItem Fields { get; }

    /// <summary>The set's id on the site (the overwrite target), when an integer.</summary>
    public long? Id => PinShareJson.AsLong(Fields.Get("id"));

    /// <summary>The name as Lisp <c>~a</c> would show it in UI text: a string, a number's text, or null.</summary>
    public string? DisplayName => Display(Fields.Get("name"));

    /// <summary>The author, for the Settings line (<c>pinshare-pin-set-text</c>, gui.lisp:1229).</summary>
    public string? Author => Display(Fields.Get("author"));

    /// <summary>The site marks the caller's own sets with <c>"mine":1</c>; only those may be overwritten (gui.lisp:1289, <c>eql 1</c>).</summary>
    public bool Mine => PinShareJson.AsLong(Fields.Get("mine")) == 1;

    /// <summary>The name cleaned for in.txt (a non-string is "").</summary>
    public string CleanName => PinShareText.Clean(PinShareJson.AsString(Fields.Get("name")));

    /// <summary>
    /// The set's items as relay-shaped items, built once per set: the relay
    /// renders in.txt at least once a second and the set only changes when a
    /// fetch lands (<c>pinshare-local-items</c>, pinshare.lisp:560).
    /// </summary>
    public (IReadOnlyList<JsonItem> Pins, IReadOnlyList<JsonItem> Arrows) LocalItems => _localItems.Value;

    private static string? Display(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        _ => null,
    };

    /// <summary>
    /// Lisp <c>build-pinshare-local-items</c> (pinshare.lisp:568): negative
    /// ids -1, -2... across pins then arrows, the cut set name as owner, the
    /// area under "floor" (falling back to "floor" when area is absent or
    /// false), never expiring (remaining -1), locked 1, and for pins the
    /// per-owner number 1..n in stored order. Only object items take an id
    /// and a number; the renderer later drops those whose floor or
    /// coordinates are not numbers, keeping the gaps (as Lisp does).
    /// </summary>
    private static (IReadOnlyList<JsonItem>, IReadOnlyList<JsonItem>) BuildLocalItems(JsonItem set)
    {
        var items = set.Get("items") is { } i ? JsonItem.From(i) : null;
        var name = PinShareText.TakeChars(PinShareText.Clean(PinShareJson.AsString(set.Get("name"))), OwnerChars);
        var owner = PinShareJson.String(name);
        var nextId = 0L;

        JsonItem? Local(JsonElement element, string[] keys)
        {
            if (JsonItem.From(element) is not { } item) return null;
            var area = item.Get("area");
            var copy = JsonItem.Empty()
                .Set("id", PinShareJson.Number(--nextId))
                .Set("owner", owner)
                .Set("floor", PinShareJson.IsTruthy(area) ? area : item.Get("floor"))
                .Set("remaining", PinShareJson.Number(-1))
                .Set("locked", PinShareJson.Number(1));
            foreach (var key in keys) copy.Set(key, item.Get(key));
            return copy;
        }

        var pins = new List<JsonItem>();
        var ownerNo = 0L;
        foreach (var element in PinShareJson.Items(items?.Get("pins")))
        {
            if (Local(element, ["x", "y", "z", "label", "color", "room"]) is { } copy)
            {
                // Per-owner number only: the owner is the set, so its own
                // 1..n never collides. The all / room numbers are the
                // channel's sequence - set pins would repeat a member's #1.
                pins.Add(copy.Set("ownerNo", PinShareJson.Number(++ownerNo)));
            }
        }
        var arrows = new List<JsonItem>();
        foreach (var element in PinShareJson.Items(items?.Get("arrows")))
        {
            if (Local(element, ["x1", "y1", "z1", "x2", "y2", "z2", "xm", "ym", "zm", "color", "room"]) is { } copy)
                arrows.Add(copy);
        }
        return (pins, arrows);
    }
}

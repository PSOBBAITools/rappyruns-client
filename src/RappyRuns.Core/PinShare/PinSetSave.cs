using System.Text;
using System.Text.Json;
using RappyRuns.Core.I18n;

namespace RappyRuns.Core.PinShare;

/// <summary>The relay server's live lists, for Save (Lisp <c>*pinshare-channel-items*</c>).</summary>
public sealed record ChannelItems(IReadOnlyList<JsonElement> Pins, IReadOnlyList<JsonElement> Arrows);

/// <summary>What <c>POST /api/pin-sets</c> (or <c>.../:id/items</c>) answered (api-client.lisp:732).</summary>
public enum PinSetSaveOutcome
{
    /// <summary>201: a new private set.</summary>
    Created,
    /// <summary>200: the set's items were replaced.</summary>
    Updated,
    /// <summary>400 / 403: the payload carries the server's message.</summary>
    Rejected,
    /// <summary>404.</summary>
    NotFound,
}

/// <summary>Why a save cannot start (Lisp <c>pinshare-save-precheck</c>, gui.lisp:1262).</summary>
public enum PinSetSaveBlock
{
    None,
    /// <summary>No quest loaded: a set is filed under the quest being played.</summary>
    NoQuest,
    /// <summary>Nothing in the channel (or not connected).</summary>
    NoItems,
    /// <summary>Overwrite only: the set in use is not the user's own (or none is in use).</summary>
    NotMine,
}

/// <summary>
/// Saving the channel's current pins as a pin set on the site: the request
/// body and the texts the Settings group shows around it. The HTTP call
/// itself belongs to the API layer.
/// </summary>
public static class PinSetSave
{
    private static readonly (string Mid, string A, string B)[] Mids = [("xm", "x1", "x2"), ("ym", "y1", "y2"), ("zm", "z1", "z2")];
    private static readonly string[] EndKeys = ["x1", "y1", "z1", "x2", "y2", "z2"];

    /// <summary>
    /// <paramref name="arrow"/> with its bend point: a relay server from
    /// before bent arrows sends none, and the site requires one - the
    /// straight midpoint, as the addon assumes (pinshare.lisp:616). Anything
    /// that is not an object, already has a numeric xm, or lacks an end
    /// coordinate passes through unchanged.
    /// </summary>
    public static JsonElement ArrowWithMid(JsonElement arrow)
    {
        if (JsonItem.From(arrow) is not { } item
            || PinShareJson.IsReal(item.Get("xm"))
            || !EndKeys.All(key => PinShareJson.IsReal(item.Get(key))))
        {
            return arrow;
        }
        foreach (var (mid, a, b) in Mids)
        {
            var value = (PinShareJson.AsDouble(item.Get(a)!.Value) + PinShareJson.AsDouble(item.Get(b)!.Value)) / 2.0;
            item.Set(mid, PinShareJson.Number(value));
        }
        return ToElement(item.WriteTo);
    }

    /// <summary>
    /// The <c>POST /api/pin-sets</c> body saving the channel's current
    /// <paramref name="pins"/> and <paramref name="arrows"/> (the server's
    /// snapshot as received) under quest <paramref name="slug"/>
    /// (pinshare.lisp:632). The site keeps what a set needs - area (it reads
    /// the relay's "floor"), coordinates, room, label, color - and drops
    /// owners, ids and numbers.
    /// </summary>
    public static string Body(string slug, IReadOnlyList<JsonElement>? pins, IReadOnlyList<JsonElement>? arrows, string? name = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, PinShareJson.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("quest", slug);
            writer.WritePropertyName("items");
            writer.WriteStartObject();
            writer.WritePropertyName("pins");
            writer.WriteStartArray();
            foreach (var pin in pins ?? []) pin.WriteTo(writer);
            writer.WriteEndArray();
            writer.WritePropertyName("arrows");
            writer.WriteStartArray();
            foreach (var arrow in arrows ?? []) ArrowWithMid(arrow).WriteTo(writer);
            writer.WriteEndArray();
            writer.WriteEndObject();
            if (name is not null) writer.WriteString("name", name);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Whether a save can start with the loaded quest's
    /// <paramref name="slugs"/> and the live <paramref name="items"/>
    /// (gui.lisp:1262). For an overwrite, pass the set in use: it must be the
    /// user's own (checked first, gui.lisp:1289).
    /// </summary>
    public static PinSetSaveBlock Precheck(IReadOnlyList<string>? slugs, ChannelItems? items, bool overwrite = false, PinSet? inUse = null)
    {
        if (overwrite && inUse is not { Mine: true }) return PinSetSaveBlock.NotMine;
        if (slugs is not { Count: > 0 }) return PinSetSaveBlock.NoQuest;
        if (items is null || (items.Pins.Count == 0 && items.Arrows.Count == 0)) return PinSetSaveBlock.NoItems;
        return PinSetSaveBlock.None;
    }

    /// <summary>The dialog text for a blocked save.</summary>
    public static string BlockText(Language language, PinSetSaveBlock block) => block switch
    {
        PinSetSaveBlock.NoQuest => Strings.Default.Tr(language, "pinshare-save-no-quest"),
        PinSetSaveBlock.NoItems => Strings.Default.Tr(language, "pinshare-save-no-items"),
        PinSetSaveBlock.NotMine => Strings.Default.Tr(language, "pinshare-save-not-mine"),
        _ => "",
    };

    /// <summary>The overwrite confirmation (gui.lisp:1295).</summary>
    public static string ConfirmOverwriteText(Language language, PinSet set, ChannelItems items) =>
        Strings.Default.Tr(language, "pinshare-save-confirm-overwrite", set.DisplayName, items.Pins.Count, items.Arrows.Count);

    /// <summary>
    /// The report after a save (gui.lisp:1237): the site URL of a new set,
    /// the counts of an update, else the server's message / error / the
    /// outcome's name. The caller re-fetches the set in use after
    /// <see cref="PinSetSaveOutcome.Updated"/> (<c>refetch-pin-set</c>) so the
    /// locked copy shows the new pins.
    /// </summary>
    public static string ReportText(Language language, PinSetSaveOutcome outcome, JsonElement? payload)
    {
        var fields = payload is { } p ? JsonItem.From(p) : null;
        object? Field(string key) => fields?.Get(key) switch
        {
            { ValueKind: JsonValueKind.String } s => s.GetString(),
            { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
            _ => null,
        };
        return outcome switch
        {
            PinSetSaveOutcome.Created => Strings.Default.Tr(language, "pinshare-save-created", Field("url")),
            PinSetSaveOutcome.Updated => Strings.Default.Tr(language, "pinshare-save-updated", Field("pins") ?? 0, Field("arrows") ?? 0),
            _ => Strings.Default.Tr(language, "pinshare-save-failed",
                Field("message") ?? Field("error") ?? (outcome == PinSetSaveOutcome.Rejected ? "rejected" : "not-found")),
        };
    }

    /// <summary>The report when the save request itself failed (auth, transport).</summary>
    public static string FailureText(Language language, Exception error) =>
        Strings.Default.Tr(language, "pinshare-save-failed", error.Message);

    private static JsonElement ToElement(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) write(writer);
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }
}

using System.Globalization;
using System.Numerics;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RappyRuns.Core.PinShare;

/// <summary>
/// One JSON object as the Lisp relay saw it: a jzon hash table. Duplicate
/// keys keep the last value (as a hash table filled in document order
/// would), and lookups answer null for an absent key. Relay-built items
/// (the pin set's local copies, an arrow given its midpoint) are made with
/// <see cref="With"/>.
/// </summary>
public sealed class JsonItem
{
    private readonly Dictionary<string, JsonElement> _fields;

    private JsonItem(Dictionary<string, JsonElement> fields) => _fields = fields;

    /// <summary>The object's fields, or null when <paramref name="element"/> is not an object (Lisp <c>hash-table-p</c> fails).</summary>
    public static JsonItem? From(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            fields.Remove(property.Name); // re-insert: last wins, in last position
            fields[property.Name] = property.Value;
        }
        return new JsonItem(fields);
    }

    /// <summary>An empty item to fill with <see cref="Set"/>.</summary>
    public static JsonItem Empty() => new(new Dictionary<string, JsonElement>(StringComparer.Ordinal));

    /// <summary>The value under <paramref name="key"/>, null when absent (Lisp <c>gethash</c> → NIL).</summary>
    public JsonElement? Get(string key) => _fields.TryGetValue(key, out var value) ? value : null;

    public IEnumerable<string> Keys => _fields.Keys;

    /// <summary>Sets <paramref name="key"/>; null leaves it absent (Lisp stores NIL, which reads back the same as a missing key).</summary>
    public JsonItem Set(string key, JsonElement? value)
    {
        if (value is { } v) _fields[key] = v;
        else _fields.Remove(key);
        return this;
    }

    /// <summary>A shallow copy with <paramref name="key"/> set.</summary>
    public JsonItem With(string key, JsonElement value)
    {
        var copy = new JsonItem(new Dictionary<string, JsonElement>(_fields, StringComparer.Ordinal));
        copy._fields[key] = value;
        return copy;
    }

    public void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in _fields)
        {
            writer.WritePropertyName(key);
            value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}

/// <summary>
/// JSON helpers that reproduce how the Lisp client (jzon) classified values:
/// a number token without '.', 'e' or 'E' is an integer (Lisp
/// <c>integerp</c>), any number is real (<c>realp</c>), JSON false is NIL like
/// an absent key, and JSON null is a (truthy) symbol.
/// </summary>
public static class PinShareJson
{
    /// <summary>Compact output with UTF-8 text left unescaped, like jzon's.</summary>
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonElement Null { get; } = JsonSerializer.SerializeToElement<object?>(null);

    public static JsonElement Number(long value) => JsonSerializer.SerializeToElement(value);

    public static JsonElement Number(double value) => JsonSerializer.SerializeToElement(value);

    public static JsonElement String(string value) => JsonSerializer.SerializeToElement(value);

    /// <summary>Parses <paramref name="text"/>, or null when it is not JSON (the relay ignores such input).</summary>
    public static JsonElement? TryParse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True for a JSON integer token (Lisp <c>integerp</c> of the jzon value).</summary>
    public static bool IsInteger(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Number } e && e.GetRawText().IndexOfAny(['.', 'e', 'E']) < 0;

    /// <summary>
    /// True for any finite JSON number (Lisp <c>realp</c>). Deviation: a
    /// float token beyond the double range (1e400) is not a number here;
    /// jzon refused the whole message.
    /// </summary>
    public static bool IsReal(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Number } e
        && (IsInteger(e) || double.IsFinite(e.GetDouble()));

    /// <summary>Lisp truthiness of a jzon value: absent and false are NIL; everything else, JSON null included, is true.</summary>
    public static bool IsTruthy(JsonElement? value) =>
        value is { } e && e.ValueKind != JsonValueKind.False;

    /// <summary>A JSON string's text, else null.</summary>
    public static string? AsString(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    /// <summary>A number's value as a double (integer tokens included).</summary>
    public static double AsDouble(JsonElement value) =>
        IsInteger(value)
            ? (double)BigInteger.Parse(value.GetRawText(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
            : value.GetDouble();

    /// <summary>An integer token as a 64-bit value, null when not an integer or out of range.</summary>
    public static long? AsLong(JsonElement? value) =>
        IsInteger(value) && value!.Value.TryGetInt64(out var n) ? n : null;

    /// <summary>
    /// A real value as in.txt prints it: an integer token as <c>~d</c> (a
    /// bignum too), a float through <see cref="PinShareText.FormatNumber(double)"/>
    /// (<c>pinshare-format-number</c>, pinshare.lisp:335).
    /// </summary>
    public static string FormatReal(JsonElement value) =>
        IsInteger(value)
            ? BigInteger.Parse(value.GetRawText(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            : PinShareText.FormatNumber(value.GetDouble());

    /// <summary>
    /// One optional in.txt field (Lisp <c>pinshare-field</c>, pinshare.lisp:345):
    /// a number printed, a string cleaned, and null / a missing key / true /
    /// false / an object or array empty.
    /// </summary>
    public static string Field(JsonElement? value)
    {
        if (IsReal(value)) return FormatReal(value!.Value);
        if (AsString(value) is { } text) return PinShareText.Clean(text);
        return "";
    }

    /// <summary>The elements of a JSON array, or empty for anything else (Lisp <c>(if (vectorp value) value #())</c>).</summary>
    public static IReadOnlyList<JsonElement> Items(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Array } e ? [.. e.EnumerateArray()] : [];

    /// <summary>Writes one message object from key/value pairs, in order (Lisp <c>pinshare-json</c>, pinshare.lisp:132).</summary>
    public static string Message(params (string Key, object Value)[] pairs)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in pairs)
            {
                writer.WritePropertyName(key);
                switch (value)
                {
                    case string s: writer.WriteStringValue(s); break;
                    case long l: writer.WriteNumberValue(l); break;
                    case int i: writer.WriteNumberValue(i); break;
                    case double d: writer.WriteNumberValue(d); break;
                    default: throw new ArgumentException($"unsupported message value {value.GetType()}");
                }
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}

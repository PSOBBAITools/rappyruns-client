using System.Globalization;
using System.Text;

namespace RappyRuns.Core.Game;

/// <summary>
/// A JSON value built the way the Lisp client builds one for com.inuoe.jzon:
/// objects are hash tables (a repeated key replaces the value in place),
/// arrays are vectors, and numbers keep their Lisp type - an integer prints as
/// one and a single-float prints with jzon's Schubfach format ("12.3", "10.0",
/// "1.0e7"). Keeping integers and floats apart matters on the wire: the
/// server's JSON reader turns "10" and "10.0" into different Lisp numbers.
/// </summary>
public abstract record LispJson
{
    public static readonly LispJson True = new JBool(true);
    public static readonly LispJson False = new JBool(false);

    public static LispJson Int(long value) => new JInteger(value);

    public static LispJson Single(float value) => new JSingle(value);

    public static LispJson Str(string value) => new JString(value);

    /// <summary>Compact text exactly as jzon:stringify writes it (no whitespace).</summary>
    public string Stringify()
    {
        var sb = new StringBuilder();
        Write(sb);
        return sb.ToString();
    }

    internal abstract void Write(StringBuilder sb);

    /// <summary>
    /// jzon %write-json-string: \" \\ \b \f \n \r \t escaped, other control
    /// characters as \uXXXX (upper-case hex), everything else raw (UTF-8).
    /// </summary>
    internal static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c <= 0x1F) sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>
    /// jzon's single-float text (schubfach.lisp write-float, ported exactly in
    /// <see cref="JzonSchubfach"/>): shortest digits, plain notation with at
    /// least one fractional digit when 1e-3 &lt;= |x| &lt; 1e7, otherwise d.ddde[-]N;
    /// zero is "0.0" ("-0.0" for negative zero).
    /// </summary>
    public static string FormatSingle(float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentException("NaN/Infinity is not JSON");
        return JzonSchubfach.WriteFloat(value);
    }
}

public sealed record JBool(bool Value) : LispJson
{
    internal override void Write(StringBuilder sb) => sb.Append(Value ? "true" : "false");
}

public sealed record JInteger(long Value) : LispJson
{
    internal override void Write(StringBuilder sb) => sb.Append(Value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>A Lisp single-float.</summary>
public sealed record JSingle(float Value) : LispJson
{
    internal override void Write(StringBuilder sb) => sb.Append(FormatSingle(Value));
}

public sealed record JString(string Value) : LispJson
{
    internal override void Write(StringBuilder sb) => WriteString(Value, sb);
}

public sealed record JArray(List<LispJson> Items) : LispJson
{
    public JArray() : this(new List<LispJson>())
    {
    }

    internal override void Write(StringBuilder sb)
    {
        sb.Append('[');
        for (var i = 0; i < Items.Count; i++)
        {
            if (i > 0) sb.Append(',');
            Items[i].Write(sb);
        }
        sb.Append(']');
    }
}

/// <summary>An equal hash table: insertion order, (setf gethash) on an existing key replaces in place.</summary>
public sealed record JObject : LispJson
{
    private readonly List<(string Key, LispJson Value)> _entries = [];

    public IReadOnlyList<(string Key, LispJson Value)> Entries => _entries;

    public LispJson? this[string key]
    {
        get => _entries.FirstOrDefault(e => e.Key == key).Value;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            var i = _entries.FindIndex(e => e.Key == key);
            if (i >= 0) _entries[i] = (key, value);
            else _entries.Add((key, value));
        }
    }

    public bool Contains(string key) => _entries.Any(e => e.Key == key);

    internal override void Write(StringBuilder sb)
    {
        sb.Append('{');
        for (var i = 0; i < _entries.Count; i++)
        {
            if (i > 0) sb.Append(',');
            WriteString(_entries[i].Key, sb);
            sb.Append(':');
            _entries[i].Value.Write(sb);
        }
        sb.Append('}');
    }
}

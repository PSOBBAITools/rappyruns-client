using System.Globalization;

namespace RappyRuns.Core.Sexp;

/// <summary>
/// A Lisp datum as the Lisp client reads and writes it (spec core §4): the
/// subset config.sexp, queue.sexp and data/quest-triggers.sexp use.
/// Lisp truthiness is explicit here: only NIL (the symbol, or the empty list)
/// is false - <c>0</c> and <c>""</c> are true (spec core, notation).
/// </summary>
public abstract record SexpNode
{
    public static readonly SSymbol Nil = new("COMMON-LISP", "NIL");
    public static readonly SSymbol T = new("COMMON-LISP", "T");

    public static SexpNode Bool(bool value) => value ? T : Nil;

    public static SKeyword Kw(string name) => new(name.ToUpperInvariant());

    public static SString Str(string value) => new(value);

    public static SInteger Int(long value) => new(value);

    public static SList List(params SexpNode[] items) => new(items);

    /// <summary>False only for NIL / the empty list.</summary>
    public bool IsTrue => !IsNil;

    public bool IsNil => this is SSymbol { IsNilSymbol: true } || this is SList { Items.Count: 0, Tail: null };

    public string? AsString => (this as SString)?.Value;

    public long? AsLong => (this as SInteger)?.Value;

    /// <summary>Integers and floats as a double (Lisp numbers), else null.</summary>
    public double? AsNumber => this switch
    {
        SInteger i => i.Value,
        SFloat f => f.Value,
        _ => null,
    };

    /// <summary>Keyword name ("EN" for :en), else null.</summary>
    public string? KeywordName => (this as SKeyword)?.Name;

    /// <summary>The elements of a proper list (NIL is empty); throws for anything else.</summary>
    public IReadOnlyList<SexpNode> Elements => this switch
    {
        SList l => l.Items,
        _ when IsNil => [],
        _ => throw new InvalidCastException($"not a list: {this}"),
    };

    public sealed override string ToString() => SexpWriter.Write(this);
}

/// <summary>A symbol other than a keyword. Names are stored upcased (the Lisp reader upcases).</summary>
public sealed record SSymbol(string? Package, string Name) : SexpNode
{
    public bool IsNilSymbol => Name == "NIL" && (Package is null || IsCl(Package));

    public bool IsTSymbol => Name == "T" && (Package is null || IsCl(Package));

    private static bool IsCl(string package) => package is "COMMON-LISP" or "CL";

    public bool Equals(SSymbol? other) =>
        other is not null && Name == other.Name &&
        (IsNilSymbol && other.IsNilSymbol || IsTSymbol && other.IsTSymbol || Package == other.Package);

    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);
}

public sealed record SKeyword(string Name) : SexpNode;

public sealed record SString(string Value) : SexpNode;

public sealed record SInteger(long Value) : SexpNode;

/// <summary>A float. <see cref="IsDouble"/> marks a double-float (written with d0); the Lisp default is single.</summary>
public sealed record SFloat(double Value, bool IsDouble = false) : SexpNode;

/// <summary>A proper list, or a dotted one when <see cref="Tail"/> is set ((a . b)).</summary>
public sealed record SList(IReadOnlyList<SexpNode> Items, SexpNode? Tail = null) : SexpNode
{
    public bool Equals(SList? other) =>
        other is not null && Items.SequenceEqual(other.Items) && Equals(Tail, other.Tail);

    public override int GetHashCode() => Items.Count;

    /// <summary>For a dotted pair (a . b): a.</summary>
    public SexpNode Car => Items[0];

    /// <summary>For a dotted pair (a . b): b; for a proper list the rest as a list.</summary>
    public SexpNode Cdr => Tail is not null && Items.Count == 1 ? Tail : new SList(Items.Skip(1).ToList(), Tail);
}

/// <summary>#(...) - read for completeness; the writer never produces it.</summary>
public sealed record SVector(IReadOnlyList<SexpNode> Items) : SexpNode
{
    public bool Equals(SVector? other) => other is not null && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => Items.Count;
}

internal static class SexpFormat
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
}

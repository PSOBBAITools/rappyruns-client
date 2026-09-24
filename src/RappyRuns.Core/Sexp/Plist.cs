namespace RappyRuns.Core.Sexp;

/// <summary>
/// A mutable property list (:KEY value ...) that keeps unknown keys and key
/// order, so a file rewritten by the C# client still carries everything the
/// Lisp client put there (spec core §3.2, §3.4).
/// </summary>
public sealed class Plist
{
    private readonly List<(string Key, SexpNode Value)> _entries = [];

    public Plist()
    {
    }

    /// <summary>From a Lisp plist; null when <paramref name="node"/> is not a keyword plist.</summary>
    public static Plist? From(SexpNode? node)
    {
        if (node is null) return null;
        if (node.IsNil) return new Plist();
        if (node is not SList { Tail: null } list || list.Items.Count % 2 != 0) return null;
        var plist = new Plist();
        for (var i = 0; i < list.Items.Count; i += 2)
        {
            if (list.Items[i] is not SKeyword k) return null;
            // getf finds the first occurrence; keep that one.
            if (!plist.Contains(k.Name)) plist._entries.Add((k.Name, list.Items[i + 1]));
        }
        return plist;
    }

    public IEnumerable<string> Keys => _entries.Select(e => e.Key);

    public int Count => _entries.Count;

    public bool Contains(string key) => IndexOf(key) >= 0;

    /// <summary>The value for <paramref name="key"/> (a keyword name, any case), or null when absent. A present NIL is returned as NIL.</summary>
    public SexpNode? Get(string key)
    {
        var i = IndexOf(key);
        return i < 0 ? null : _entries[i].Value;
    }

    /// <summary>Sets a key; a new key goes to the front like Lisp's (setf getf).</summary>
    public void Set(string key, SexpNode value)
    {
        var name = key.ToUpperInvariant();
        var i = IndexOf(name);
        if (i >= 0) _entries[i] = (name, value);
        else _entries.Insert(0, (name, value));
    }

    public bool Remove(string key)
    {
        var i = IndexOf(key);
        if (i < 0) return false;
        _entries.RemoveAt(i);
        return true;
    }

    public Plist Clone()
    {
        var copy = new Plist();
        copy._entries.AddRange(_entries);
        return copy;
    }

    public SexpNode ToSexp() =>
        _entries.Count == 0
            ? SexpNode.Nil
            : new SList(_entries.SelectMany(e => new[] { (SexpNode)new SKeyword(e.Key), e.Value }).ToList());

    private int IndexOf(string key)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            if (string.Equals(_entries[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RappyRuns.Core.Sexp;

/// <summary>
/// Reads Lisp data (spec core §4.3): lists, dotted pairs, #( ) vectors,
/// strings, integers, floats (e/s/f/d/l exponent markers), keywords,
/// package-qualified and bare symbols (upcased, |escaped| kept), ; and #| |#
/// comments. No reader macros beyond that - nothing is ever evaluated.
/// </summary>
public sealed partial class SexpReader
{
    private readonly string _text;
    private int _pos;

    private SexpReader(string text) => _text = text;

    /// <summary>Reads the first datum of <paramref name="text"/>.</summary>
    public static SexpNode ReadOne(string text)
    {
        var reader = new SexpReader(text);
        return reader.Read() ?? throw new SexpException("no datum");
    }

    /// <summary>Reads every top-level datum.</summary>
    public static List<SexpNode> ReadAll(string text)
    {
        var reader = new SexpReader(text);
        var all = new List<SexpNode>();
        while (reader.Read() is { } node) all.Add(node);
        return all;
    }

    /// <summary>
    /// Like the Lisp client's read-sexp-file: the first datum of a UTF-8 file,
    /// or null when the file is missing, empty or malformed.
    /// </summary>
    public static SexpNode? TryReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path, Encoding.UTF8);
            return new SexpReader(text).Read();
        }
        catch (Exception e) when (e is SexpException or IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }

    private SexpNode? Read()
    {
        SkipWhitespaceAndComments();
        if (_pos >= _text.Length) return null;
        var c = _text[_pos];
        switch (c)
        {
            case '(':
                _pos++;
                return ReadListBody();
            case ')':
                throw new SexpException($"unexpected ) at {_pos}");
            case '"':
                _pos++;
                return new SString(ReadStringBody());
            case '\'':
                _pos++;
                var quoted = Read() ?? throw new SexpException("nothing after '");
                return new SList([new SSymbol("COMMON-LISP", "QUOTE"), quoted]);
            case '#':
                if (_pos + 1 < _text.Length && _text[_pos + 1] == '(')
                {
                    _pos += 2;
                    var list = ReadListBody();
                    if (list is not SList { Tail: null } proper && !list.IsNil) throw new SexpException("dotted vector");
                    return new SVector(list.Elements);
                }
                throw new SexpException($"unsupported # syntax at {_pos}");
            default:
                return ReadAtom();
        }
    }

    private SexpNode ReadListBody()
    {
        var items = new List<SexpNode>();
        SexpNode? tail = null;
        while (true)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _text.Length) throw new SexpException("unclosed (");
            var c = _text[_pos];
            if (c == ')')
            {
                _pos++;
                break;
            }
            if (c == '.' && items.Count > 0 && IsDelimiter(_pos + 1))
            {
                _pos++;
                tail = Read() ?? throw new SexpException("nothing after .");
                SkipWhitespaceAndComments();
                if (_pos >= _text.Length || _text[_pos] != ')') throw new SexpException("more than one datum after .");
                _pos++;
                break;
            }
            items.Add(Read() ?? throw new SexpException("unclosed ("));
        }
        if (items.Count == 0 && tail is null) return SexpNode.Nil;
        // (a . nil) is just (a).
        if (tail is not null && tail.IsNil) tail = null;
        return new SList(items, tail);
    }

    private string ReadStringBody()
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _text.Length) throw new SexpException("unclosed string");
            var c = _text[_pos++];
            if (c == '"') return sb.ToString();
            if (c == '\\')
            {
                if (_pos >= _text.Length) throw new SexpException("unclosed string");
                c = _text[_pos++];
            }
            sb.Append(c);
        }
    }

    private SexpNode ReadAtom()
    {
        // Collect a token; |...| parts keep their case and characters,
        // \x escapes one character.
        var sb = new StringBuilder();
        var escaped = new List<bool>();
        var anyEscape = false;
        while (_pos < _text.Length && !IsDelimiter(_pos))
        {
            var c = _text[_pos];
            if (c == '|')
            {
                _pos++;
                anyEscape = true;
                while (_pos < _text.Length && _text[_pos] != '|')
                {
                    sb.Append(_text[_pos++]);
                    escaped.Add(true);
                }
                if (_pos >= _text.Length) throw new SexpException("unclosed |");
                _pos++;
            }
            else if (c == '\\')
            {
                _pos++;
                if (_pos >= _text.Length) throw new SexpException("dangling \\");
                sb.Append(_text[_pos++]);
                escaped.Add(true);
                anyEscape = true;
            }
            else
            {
                sb.Append(c);
                escaped.Add(false);
                _pos++;
            }
        }
        var token = sb.ToString();
        if (token.Length == 0) throw new SexpException($"empty token at {_pos}");

        if (!anyEscape && ParseNumber(token) is { } number) return number;

        // Upcase the unescaped characters, like the standard readtable.
        var upper = new StringBuilder(token.Length);
        for (var i = 0; i < token.Length; i++)
            upper.Append(escaped[i] ? token[i] : char.ToUpperInvariant(token[i]));
        var name = upper.ToString();

        // Package markers are only the unescaped colons.
        var colon = -1;
        for (var i = 0; i < token.Length; i++)
        {
            if (token[i] == ':' && !escaped[i])
            {
                colon = i;
                break;
            }
        }
        if (colon == 0) return new SKeyword(name[1..]);
        if (colon > 0)
        {
            var package = name[..colon];
            var rest = name[(colon + 1)..];
            if (rest.StartsWith(':')) rest = rest[1..]; // pkg::sym
            if (package == "KEYWORD") return new SKeyword(rest);
            return new SSymbol(package is "CL" ? "COMMON-LISP" : package, rest);
        }
        return name switch
        {
            "NIL" => SexpNode.Nil,
            "T" => SexpNode.T,
            _ => new SSymbol(null, name),
        };
    }

    [GeneratedRegex(@"^[+-]?\d+\.?$")]
    private static partial Regex IntegerToken();

    [GeneratedRegex(@"^([+-]?(?:\d+\.\d*|\.\d+|\d+))(?:([esfdlESFDL])([+-]?\d+))?$")]
    private static partial Regex FloatToken();

    private static SexpNode? ParseNumber(string token)
    {
        if (IntegerToken().IsMatch(token))
        {
            var digits = token.TrimEnd('.');
            return long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? new SInteger(value)
                : throw new SexpException($"integer out of range: {token}");
        }
        var m = FloatToken().Match(token);
        if (!m.Success) return null;
        // A float needs a decimal point or an exponent ("12" is an integer, handled above).
        var mantissa = m.Groups[1].Value;
        if (!mantissa.Contains('.') && !m.Groups[2].Success) return null;
        var marker = m.Groups[2].Success ? char.ToLowerInvariant(m.Groups[2].Value[0]) : 'e';
        var text = m.Groups[2].Success ? $"{mantissa}e{m.Groups[3].Value}" : mantissa;
        var d = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        var isDouble = marker is 'd' or 'l';
        return new SFloat(isDouble ? d : (float)d, isDouble);
    }

    private bool IsDelimiter(int pos)
    {
        if (pos >= _text.Length) return true;
        var c = _text[pos];
        return char.IsWhiteSpace(c) || c is '(' or ')' or '"' or ';' or '\'';
    }

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _text.Length)
        {
            var c = _text[_pos];
            if (char.IsWhiteSpace(c) || c == '﻿')
            {
                _pos++;
            }
            else if (c == ';')
            {
                while (_pos < _text.Length && _text[_pos] != '\n') _pos++;
            }
            else if (c == '#' && _pos + 1 < _text.Length && _text[_pos + 1] == '|')
            {
                var end = _text.IndexOf("|#", _pos + 2, StringComparison.Ordinal);
                if (end < 0) throw new SexpException("unclosed #|");
                _pos = end + 2;
            }
            else
            {
                return;
            }
        }
    }
}

public sealed class SexpException(string message) : Exception(message);

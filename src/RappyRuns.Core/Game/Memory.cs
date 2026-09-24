using System.Text;

namespace RappyRuns.Core.Game;

/// <summary>
/// Process memory access (memory.lisp:7 read-block). The live
/// implementation (RappyRuns.Win.Game.LiveReader) reads the 32-bit PSOBB
/// process; <see cref="MockReader"/> serves tests. All multi-byte values are
/// little-endian (x86), addresses are 32-bit.
/// </summary>
public interface IMemoryReader
{
    /// <summary>
    /// <paramref name="size"/> bytes at <paramref name="address"/>, or null when
    /// the memory is unreadable. A single failure is normal (pointer chases
    /// during warps and reloads), so callers treat null as "not this frame".
    /// </summary>
    byte[]? ReadBlock(long address, int size);

    /// <summary>Title of the attached game window (the recorder's gdigrab title= input), or null.</summary>
    string? WindowTitle => null;
}

/// <summary>
/// Memory image made of (address, bytes) regions (memory.lisp:20 mock-reader).
/// A read succeeds only when it lies entirely inside one region; the first
/// matching region wins, like the Lisp loop over the region list.
/// </summary>
public sealed class MockReader(IEnumerable<(long Address, byte[] Bytes)> regions) : IMemoryReader
{
    private readonly List<(long Address, byte[] Bytes)> _regions = regions.ToList();

    public MockReader(params (long Address, byte[] Bytes)[] regions) : this((IEnumerable<(long, byte[])>)regions)
    {
    }

    /// <summary>The regions, first match wins; tests prepend overlays (tests-quests gdv-reader).</summary>
    public List<(long Address, byte[] Bytes)> Regions => _regions;

    public byte[]? ReadBlock(long address, int size)
    {
        foreach (var (baseAddress, bytes) in _regions)
        {
            if (address >= baseAddress && address + size <= baseAddress + bytes.Length)
            {
                var result = new byte[size];
                Array.Copy(bytes, address - baseAddress, result, 0, size);
                return result;
            }
        }
        return null;
    }
}

/// <summary>Decoding helpers (memory.lisp:34-99), bit-exact with the Lisp client.</summary>
public static class MemoryDecode
{
    public static int U16(byte[] bytes, int offset) => bytes[offset] | (bytes[offset + 1] << 8);

    public static uint U32(byte[] bytes, int offset) =>
        (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));

    /// <summary>
    /// IEEE 754 single from its bits (memory.lisp:46 u32-float). Parity details:
    /// exponent 255 (Inf/NaN) clamps to ±3.4e38 by the sign bit, and a negative
    /// zero decodes as +0.0 (the Lisp computes sign*mantissa as an integer first).
    /// </summary>
    public static float U32Float(uint bits)
    {
        var negative = (bits & 0x8000_0000u) != 0;
        var expo = (bits >> 23) & 0xFF;
        var mant = bits & 0x7F_FFFF;
        if (expo == 255) return negative ? -3.4e38f : 3.4e38f;
        if (expo == 0 && mant == 0) return 0f;
        return BitConverter.Int32BitsToSingle(unchecked((int)bits));
    }

    /// <summary>IEEE 754 double from its bits (memory.lisp:73 u64-double), clamped to ±1.7e308 like <see cref="U32Float"/>.</summary>
    public static double U64Double(ulong bits)
    {
        var negative = (bits & 0x8000_0000_0000_0000ul) != 0;
        var expo = (bits >> 52) & 0x7FF;
        var mant = bits & 0xF_FFFF_FFFF_FFFFul;
        if (expo == 2047) return negative ? -1.7e308 : 1.7e308;
        if (expo == 0 && mant == 0) return 0d;
        return BitConverter.Int64BitsToDouble(unchecked((long)bits));
    }

    /// <summary>NUL-terminated UTF-16LE, BMP code units only (memory.lisp:89 decode-utf16-z).</summary>
    public static string Utf16Z(byte[] bytes, int offset = 0, int? length = null)
    {
        var end = offset + (length ?? bytes.Length - offset);
        var sb = new StringBuilder();
        for (var i = offset; i < end - 1; i += 2)
        {
            var code = U16(bytes, i);
            if (code == 0) break;
            sb.Append((char)code);
        }
        return sb.ToString();
    }

    /// <summary>NUL-terminated printable ASCII (32..126 kept, others skipped) - guild cards (psobb.lisp:289).</summary>
    public static string AsciiZ(byte[] bytes, int offset, int length)
    {
        var sb = new StringBuilder();
        for (var i = offset; i < offset + length; i++)
        {
            var b = bytes[i];
            if (b == 0) break;
            if (b is >= 32 and <= 126) sb.Append((char)b);
        }
        return sb.ToString();
    }
}

/// <summary>Typed reads over <see cref="IMemoryReader"/> (memory.lisp:57-99). Null = unreadable.</summary>
public static class MemoryReaderExtensions
{
    public static int? ReadU8(this IMemoryReader reader, long address) =>
        reader.ReadBlock(address, 1) is { } b ? b[0] : null;

    public static int? ReadU16(this IMemoryReader reader, long address) =>
        reader.ReadBlock(address, 2) is { } b ? MemoryDecode.U16(b, 0) : null;

    public static uint? ReadU32(this IMemoryReader reader, long address) =>
        reader.ReadBlock(address, 4) is { } b ? MemoryDecode.U32(b, 0) : null;

    public static float? ReadF32(this IMemoryReader reader, long address) =>
        reader.ReadU32(address) is { } bits ? MemoryDecode.U32Float(bits) : null;

    public static double? ReadF64(this IMemoryReader reader, long address) =>
        reader.ReadBlock(address, 8) is { } b
            ? MemoryDecode.U64Double(MemoryDecode.U32(b, 0) | ((ulong)MemoryDecode.U32(b, 4) << 32))
            : null;

    public static string? ReadUtf16String(this IMemoryReader reader, long address, int maxBytes) =>
        reader.ReadBlock(address, maxBytes) is { } b ? MemoryDecode.Utf16Z(b) : null;
}

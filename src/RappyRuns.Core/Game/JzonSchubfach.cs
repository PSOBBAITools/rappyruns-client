using System.Numerics;

namespace RappyRuns.Core.Game;

/// <summary>
/// A line-by-line port of com.inuoe.jzon's single-float writer
/// (jzon-v1.1.4 src/schubfach.lisp %write-float), which is how the Lisp
/// client's run JSON spells every float. It is a port of jsoniter-scala's
/// Schubfach, <b>including two transcription slips</b> the C# must keep for
/// bit-exact output: the exact-integer test is (m2 &lt;&lt; e2) AND e2, and the
/// rounding step compares against (vb4 - vb4 + 4) where the original has
/// (vb4 - vbr + 4). They make a few values print a different (still
/// round-tripping) last digit than .NET or Java would - e.g. Float.MaxValue
/// as 3.4028234e38 - so this is not replaceable by ToString("R").
/// Arithmetic mirrors the %int32/%int64 wrap-around macros: C# int/long
/// arithmetic is unchecked and shift counts are masked the same way.
/// </summary>
internal static class JzonSchubfach
{
    private static readonly ulong[] Offsets =
    [
        5088146770730811392, 5088146770730811392, 5088146770730811392, 5088146770730811392,
        5088146770730811392, 5088146770730811392, 5088146770730811392, 5088146770730811392,
        4889916394579099648, 4889916394579099648, 4889916394579099648, 4610686018427387904,
        4610686018427387904, 4610686018427387904, 4610686018427387904, 4323355642275676160,
        4323355642275676160, 4323355642275676160, 4035215266123964416, 4035215266123964416,
        4035215266123964416, 3746993889972252672, 3746993889972252672, 3746993889972252672,
        3746993889972252672, 3458764413820540928, 3458764413820540928, 3458764413820540928,
        3170534127668829184, 3170534127668829184, 3170534127668829184, 2882303760517117440,
        2882303760517117440, 2882303760517117440, 2882303760517117440, 2594073385265405696,
        2594073385265405696, 2594073385265405696, 2305843009203693952, 2305843009203693952,
        2305843009203693952, 2017612633060982208, 2017612633060982208, 2017612633060982208,
        2017612633060982208, 1729382256910170464, 1729382256910170464, 1729382256910170464,
        1441151880758548720, 1441151880758548720, 1441151880758548720, 1152921504606845976,
        1152921504606845976, 1152921504606845976, 1152921504606845976, 864691128455135132,
        864691128455135132, 864691128455135132, 576460752303423478, 576460752303423478,
        576460752303423478, 576460752303423478, 576460752303423478, 576460752303423478,
        576460752303423478,
    ];

    /// <summary>*%digits*: index 10j+k holds ('0'+k)&lt;&lt;8 | ('0'+j) - tens in the low byte.</summary>
    private static readonly ushort[] Digits = BuildDigits();

    /// <summary>*%gs*: the 128-bit powers of five table, generated exactly as the Lisp does.</summary>
    private static readonly long[] Gs = BuildGs();

    private static ushort[] BuildDigits()
    {
        var ds = new ushort[100];
        var i = 0;
        for (var j = 0; j < 10; j++)
        {
            for (var k = 0; k < 10; k++) ds[i++] = (ushort)((('0' + k) << 8) | ('0' + j));
        }
        return ds;
    }

    private static long[] BuildGs()
    {
        var gs = new long[1234];
        var mask = (BigInteger.One << 63) - 1;
        var i = 0;
        var pow5 = BigInteger.One;
        while (i < 650)
        {
            var shift = 126 - (int)pow5.GetBitLength();
            var av = (shift >= 0 ? pow5 << shift : pow5 >> -shift) + 1;
            gs[648 - i] = (long)((av >> 63) & mask);
            gs[649 - i] = (long)(av & mask);
            pow5 *= 5;
            i += 2;
        }
        pow5 = 5;
        while (i < 1234)
        {
            var inv = BigInteger.Divide(BigInteger.One << ((int)pow5.GetBitLength() + 125), pow5) + 1;
            gs[i] = (long)((inv >> 63) & mask);
            gs[i + 1] = (long)(inv & mask);
            pow5 *= 5;
            i += 2;
        }
        return gs;
    }

    private static int Rop2(long g, int cp)
    {
        var x = Math.BigMul(g, (long)cp << 32, out _);
        return unchecked((int)((ulong)x >> 31)) | (int)((uint)unchecked(-(int)x) >> 31);
    }

    private static int DigitCount(int q0)
    {
        var bits = 64 - BitOperations.LeadingZeroCount((ulong)(uint)q0);
        return unchecked((int)((long)(Offsets[64 - bits] + (ulong)(uint)q0) >> 58));
    }

    private static void WriteFractionDigits(int q, int p, int posLim, char[] buf)
    {
        var q0 = q;
        var pos = p;
        while (pos > posLim)
        {
            var q1 = unchecked((int)((long)q0 * 1374389535 >> 37));
            var d = Digits[q0 - q1 * 100];
            buf[pos - 1] = (char)(d & 0x7F);
            buf[pos] = (char)((d >> 8) & 0x7F);
            q0 = q1;
            pos -= 2;
        }
    }

    private static int WriteSignificantFractionDigits32(int q, int p, int posLim, char[] buf)
    {
        var q0 = q;
        int q1;
        var pos = p;
        while (true)
        {
            var qp = (long)q0 * 1374389535;
            q1 = unchecked((int)(qp >> 37));
            if ((qp & 0x1FC0000000) != 0) break;
            q0 = q1;
            pos -= 2;
        }
        var d = Digits[q0 - q1 * 100];
        buf[pos - 1] = (char)(d & 0x7F);
        buf[pos] = (char)((d >> 8) & 0x7F);
        var lastPos = pos;
        if (d > 0x3039) lastPos++;
        WriteFractionDigits(q1, pos - 2, posLim, buf);
        return lastPos;
    }

    private static int Write2Digits(int q0, int pos, char[] buf)
    {
        var d = Digits[q0];
        buf[pos] = (char)(d & 0x7F);
        buf[pos + 1] = (char)((d >> 8) & 0x7F);
        return pos + 2;
    }

    private static void WritePositiveIntDigits(int q, int p, char[] buf)
    {
        var q0 = q;
        var pos = p;
        while (true)
        {
            pos -= 2;
            if (q0 < 100) break;
            var q1 = unchecked((int)((long)q0 * 1374389535 >> 37));
            var d = Digits[q0 - q1 * 100];
            buf[pos] = (char)(d & 0x7F);
            buf[pos + 1] = (char)((d >> 8) & 0x7F);
            q0 = q1;
        }
        if (q0 < 10)
        {
            buf[pos + 1] = (char)('0' + q0);
        }
        else
        {
            var d = Digits[q0];
            buf[pos] = (char)(d & 0x7F);
            buf[pos + 1] = (char)((d >> 8) & 0x7F);
        }
    }

    /// <summary>jzon (stringify single-float): the exact text the Lisp client writes.</summary>
    public static string WriteFloat(float x)
    {
        var buf = new char[15];
        var n = Write(x, buf);
        return new string(buf, 0, n);
    }

    private static int Write(float x, char[] buf)
    {
        var bits = (uint)BitConverter.SingleToInt32Bits(x);
        var pos = 0;
        if ((bits & 0x8000_0000u) != 0) buf[pos++] = '-';
        if (x == 0f)
        {
            buf[pos] = '0';
            buf[pos + 1] = '.';
            buf[pos + 2] = '0';
            return pos + 3;
        }
        unchecked
        {
            var ieeeExponent = (int)((bits >> 23) & 0xFF);
            var ieeeMantissa = (int)(bits & 0x7F_FFFF);
            var e2 = ieeeExponent - 150;
            var m2 = ieeeMantissa | 0x80_0000;
            int m10;
            int e10 = 0;
            if (e2 == 0)
            {
                m10 = m2;
            }
            else if (e2 >= -23 && e2 <= 0 && ((m2 << e2) & e2) == 0)
            {
                m10 = m2 >> -e2;
            }
            else
            {
                var e10Corr = 0;
                var e2Corr = 0;
                var cblCorr = 2;
                if (ieeeExponent == 0)
                {
                    e2 = -149;
                    m2 = ieeeMantissa;
                    if (ieeeMantissa < 8)
                    {
                        m2 *= 10;
                        e10Corr = 1;
                    }
                }
                else if (ieeeExponent == 255)
                {
                    throw new ArgumentException("IllegalNumberError");
                }
                else if (ieeeMantissa == 0 && ieeeExponent > 1)
                {
                    e2Corr = 131007;
                    cblCorr = 1;
                }
                e10 = (e2 * 315653 - e2Corr) >> 20;
                var g = Gs[(e10 + 324) << 1] + 1;
                var h = ((-e10 * 108853) >> 15) + e2 + 1;
                var cb = m2 << 2;
                var vbCorr = (m2 & 1) - 1;
                var vb = Rop2(g, cb << h);
                var vbl = Rop2(g, (cb - cblCorr) << h) + vbCorr;
                var vbr = Rop2(g, (cb + 2) << h) - vbCorr;
                bool fallback;
                m10 = 0;
                if (vb < 400)
                {
                    fallback = true;
                }
                else
                {
                    // divide a positive int by 40
                    m10 = (int)((long)vb * 107374183 >> 32);
                    var vb40 = m10 * 40;
                    var diff = vbl - vb40;
                    if (((vb40 - vbr + 40) ^ diff) >= 0)
                    {
                        fallback = true;
                    }
                    else
                    {
                        m10 += (int)((uint)~diff >> 31);
                        e10 += 1;
                        fallback = false;
                    }
                }
                if (fallback)
                {
                    m10 = vb >> 2;
                    var vb4 = vb & unchecked((int)0xFFFFFFFC);
                    var diff = vbl - vb4;
                    // jzon writes (+ vb4 (- vb4) 4) - i.e. 4 - where Schubfach has vb4 - vbr + 4.
                    var t = ((vb4 - vb4 + 4) ^ diff) < 0 ? diff : (vb & 0x3) + (m10 & 0x1) - 3;
                    m10 += (int)((uint)~t >> 31);
                    e10 -= e10Corr;
                }
            }

            var len = DigitCount(m10);
            e10 = e10 + len - 1;
            if (e10 < -3 || e10 >= 7)
            {
                var lastPos = WriteSignificantFractionDigits32(m10, pos + len, pos, buf);
                buf[pos] = buf[pos + 1];
                buf[pos + 1] = '.';
                if (lastPos < pos + 3)
                {
                    buf[lastPos] = '0';
                    pos = lastPos + 1;
                }
                else
                {
                    pos = lastPos;
                }
                buf[pos] = 'e';
                buf[pos + 1] = '-';
                pos++;
                if (e10 < 0)
                {
                    e10 = -e10;
                    pos++;
                }
                if (e10 < 10)
                {
                    buf[pos] = (char)('0' + e10);
                    return pos + 1;
                }
                return Write2Digits(e10, pos, buf);
            }
            if (e10 < 0)
            {
                var dotPos = pos + 1;
                buf[pos] = '0';
                buf[pos + 2] = '0';
                buf[pos + 3] = '0';
                pos -= e10;
                var lastPos = WriteSignificantFractionDigits32(m10, pos + len, pos, buf);
                buf[dotPos] = '.';
                return lastPos;
            }
            if (e10 < len - 1)
            {
                var lastPos = WriteSignificantFractionDigits32(m10, pos + len, pos, buf);
                for (var i = 0; i < e10 + 1; i++)
                {
                    buf[pos] = buf[pos + 1];
                    pos++;
                }
                buf[pos] = '.';
                return lastPos;
            }
            pos += len;
            WritePositiveIntDigits(m10, pos, buf);
            buf[pos] = '.';
            buf[pos + 1] = '0';
            return pos + 2;
        }
    }
}

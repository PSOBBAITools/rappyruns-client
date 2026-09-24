using RappyRuns.Core.Sexp;

namespace RappyRuns.Tests;

public class SexpTests
{
    [Fact]
    public void SingleFloatsPrintLikeLisp()
    {
        foreach (var g in Golden.Load("sexp-print.json").GetProperty("singles").EnumerateArray())
        {
            var value = BitConverter.UInt32BitsToSingle(g.GetProperty("bits").GetUInt32());
            Assert.Equal(g.GetProperty("text").GetString(), SexpWriter.FormatFloat(new SFloat(value)));
        }
    }

    [Fact]
    public void DoubleFloatsPrintLikeLisp()
    {
        foreach (var g in Golden.Load("sexp-print.json").GetProperty("doubles").EnumerateArray())
        {
            Assert.Equal(g.GetProperty("text").GetString(), SexpWriter.FormatFloat(new SFloat(g.GetProperty("value").GetDouble(), IsDouble: true)));
        }
    }

    [Fact]
    public void DataRoundTripsToTheSameText()
    {
        foreach (var g in Golden.Load("sexp-print.json").GetProperty("data").EnumerateArray())
        {
            var text = g.GetString()!;
            Assert.Equal(text, SexpWriter.Write(SexpReader.ReadOne(text)));
        }
    }

    [Fact]
    public void ReadsTheRealConfigShape()
    {
        const string config = "(:PINSHARE-ALLOWED COMMON-LISP:T :PINSHARE-CHANNEL \"teapot\" :OVERLAY-POSITION (0.7894558 0.020304569) :OVERLAY-CORNER :CUSTOM :TRACKING-PRIVATE COMMON-LISP:NIL :LANGUAGE :JA :RECORD-DIR \"\" :SERVER-URL \"https://rappyruns-production.up.railway.app\" :API-TOKEN \"tok\")";
        var plist = Plist.From(SexpReader.ReadOne(config))!;
        Assert.True(plist.Get("pinshare-allowed")!.IsTrue);
        Assert.True(plist.Get("tracking-private")!.IsNil);
        Assert.Equal("JA", plist.Get("language")!.KeywordName);
        Assert.Equal("", plist.Get("record-dir")!.AsString);
        var position = plist.Get("overlay-position")!.Elements;
        Assert.Equal(0.7894558f, (float)position[0].AsNumber!.Value);
        Assert.Null(plist.Get("missing"));
    }

    [Theory]
    [InlineData("t", true)]
    [InlineData("nil", false)]
    [InlineData("CL:T", true)]
    [InlineData("common-lisp:nil", false)]
    [InlineData("()", false)]
    [InlineData("0", true)]
    [InlineData("\"\"", true)]
    public void LispTruthiness(string text, bool expected) =>
        Assert.Equal(expected, SexpReader.ReadOne(text).IsTrue);

    [Fact]
    public void ReadsCommentsDottedPairsAndNumbers()
    {
        var all = SexpReader.ReadAll("""
            ;; a comment
            ((5 . 1234) ("Resta" . 2)) ; trailing
            #| block |# (1 -2 +3 1.5 2e3 1.5d0 .5 7.)
            """);
        Assert.Equal(2, all.Count);
        var pair = (SList)all[0].Elements[0];
        Assert.Equal(5, pair.Car.AsLong);
        Assert.Equal(1234, pair.Cdr.AsLong);
        var numbers = all[1].Elements;
        Assert.Equal([1L, -2L, 3L], numbers.Take(3).Select(n => n.AsLong!.Value));
        Assert.Equal(1.5, numbers[3].AsNumber);
        Assert.Equal(2000, numbers[4].AsNumber);
        Assert.True(((SFloat)numbers[5]).IsDouble);
        Assert.Equal(0.5, numbers[6].AsNumber);
        Assert.Equal(7, numbers[7].AsLong);
    }

    [Fact]
    public void KeywordsAndSymbolsAreCaseInsensitive()
    {
        Assert.Equal(SexpNode.Kw("server-url"), SexpReader.ReadOne(":Server-Url"));
        Assert.Equal(new SSymbol(null, "FOO"), SexpReader.ReadOne("foo"));
        Assert.Equal(new SSymbol(null, "Foo"), SexpReader.ReadOne("|Foo|"));
    }

    [Fact]
    public void StringsKeepEscapesAndNewlines()
    {
        var s = SexpReader.ReadOne("\"a\\\"b\\\\c\nd\"").AsString;
        Assert.Equal("a\"b\\c\nd", s);
        Assert.Equal("\"a\\\"b\\\\c\nd\"", SexpWriter.Write(SexpNode.Str(s!)));
    }

    [Theory]
    [InlineData("(1 2")]
    [InlineData("\"open")]
    [InlineData(")")]
    [InlineData("#.(evil)")]
    public void MalformedInputThrows(string text) =>
        Assert.Throws<SexpException>(() => SexpReader.ReadOne(text));

    [Fact]
    public void TryReadFileIsNullForBrokenOrMissingFiles()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "c.sexp");
            Assert.Null(SexpReader.TryReadFile(path));
            File.WriteAllText(path, "(:a 1");
            Assert.Null(SexpReader.TryReadFile(path));
            SexpWriter.WriteFile(path, SexpReader.ReadOne("(:a 1)"));
            Assert.Equal("(:A 1)", File.ReadAllText(path));
            Assert.Equal(1, Plist.From(SexpReader.TryReadFile(path))!.Get("a")!.AsLong);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void PlistKeepsUnknownKeysAndPushesNewOnesToTheFront()
    {
        var plist = Plist.From(SexpReader.ReadOne("(:GHOST-VIDEO COMMON-LISP:T :LANGUAGE :EN)"))!;
        plist.Set("language", SexpNode.Kw("ja"));
        plist.Set("debug", SexpNode.T);
        Assert.Equal("(:DEBUG COMMON-LISP:T :GHOST-VIDEO COMMON-LISP:T :LANGUAGE :JA)", SexpWriter.Write(plist.ToSexp()));
    }
}

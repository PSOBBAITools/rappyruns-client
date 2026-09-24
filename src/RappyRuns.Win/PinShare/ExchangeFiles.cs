using System.Text;

namespace RappyRuns.Win.PinShare;

/// <summary>
/// Reading out.txt and replacing in.txt in the addon's exchange folder
/// (pinshare-win32.lisp:60-89). The client only ever touches these two
/// files - never the game process.
/// </summary>
public static class ExchangeFiles
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The text of <paramref name="path"/>, "" when it is missing or
    /// momentarily locked by the addon. Bytes that are not UTF-8 (a name in
    /// some other encoding) fall back to one character per byte rather than
    /// losing the commands. Opened with full sharing so the addon's own
    /// writes never fail because of us.
    /// </summary>
    public static string ReadText(string path)
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>
    /// Replaces <paramref name="inPath"/> with <paramref name="text"/> (UTF-8,
    /// no BOM) through <paramref name="tmpPath"/> + MoveFileEx(REPLACE_EXISTING),
    /// so the addon never reads a half-written list. False on failure (the
    /// addon has the file open this instant): the caller stays dirty and
    /// retries next tick.
    /// </summary>
    public static bool ReplaceInbox(string inPath, string tmpPath, string text)
    {
        try
        {
            File.WriteAllBytes(tmpPath, Utf8NoBom.GetBytes(text));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return NativeMethods.MoveFileEx(tmpPath, inPath, NativeMethods.MovefileReplaceExisting);
    }
}

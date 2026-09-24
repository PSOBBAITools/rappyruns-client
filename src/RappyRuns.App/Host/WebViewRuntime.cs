using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace RappyRuns.App.Host;

/// <summary>
/// Checks for the Evergreen WebView2 Runtime before any window exists. It
/// ships with Windows 11 and nearly every Windows 10, but when it is missing
/// the user gets a bilingual message and the download page instead of a crash.
/// </summary>
internal static class WebViewRuntime
{
    private const string DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    public static bool EnsureAvailable()
    {
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // No config is read yet at this point, so say it in both languages.
            var answer = MessageBox.Show(
                "Rappy Runs Client needs the Microsoft Edge WebView2 Runtime, which is not installed on this PC.\n" +
                "Install it, then start the client again. Open the download page now?\n\n" +
                "Rappy Runs Client の動作には Microsoft Edge WebView2 ランタイムが必要ですが、この PC には入っていません。\n" +
                "インストールしてからクライアントを起動し直してください。ダウンロードページを開きますか?",
                "Rappy Runs Client",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
            return false;
        }
    }
}

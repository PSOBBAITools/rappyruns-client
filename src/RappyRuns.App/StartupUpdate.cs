using RappyRuns.Core;
using RappyRuns.Core.I18n;
using RappyRuns.Core.Media;
using RappyRuns.Core.Update;
using RappyRuns.Host;
using RappyRuns.Win.Shell;
using RappyRuns.Win.Update;

namespace RappyRuns.App;

/// <summary>
/// The automatic update pass before the main window exists (startup-auto-update,
/// gui.lisp:1718): an outdated build never flashes its window just to quit.
/// When a newer release is out and the folder is writable, the zip downloads
/// behind a small splash, the startup marker is written (a bridge helper may be
/// waiting on this very process) and the hand-over starts the new exe; this
/// process then exits. Every other outcome returns a note for the Settings tab.
/// </summary>
internal static class StartupUpdate
{
    public static StartupUpdateNote Run(SelfUpdater updater, string configDir, Language language, bool multiInstance)
    {
        (UpdateDecision Decision, ReleaseInfo? Release) check;
        try
        {
            var rejected = UpdateFiles.RejectedTag(configDir);
            check = Task.Run(() => updater.StartupCheckAsync(rejected)).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            RecordingLog.Write("startup update check failed: " + e.Message);
            return StartupUpdateNote.None;
        }
        switch (check.Decision)
        {
            case UpdateDecision.UpToDate: return StartupUpdateNote.UpToDate;
            case UpdateDecision.Rejected: return StartupUpdateNote.Rejected;
            case UpdateDecision.NotWritable: return StartupUpdateNote.NotWritable;
            case UpdateDecision.Apply: break;
            default: return StartupUpdateNote.None;
        }

        var release = check.Release!;
        string? zip = null;
        using (var splash = new Splash(Msg.Of("update-downloading", release.Tag, 0, null).Render(language)))
        {
            splash.Shown += async (_, _) =>
            {
                var progress = SelfUpdater.MegabyteProgress((mb, total) =>
                    splash.SetText(Msg.Of("update-downloading", release.Tag, mb, total).Render(language)));
                try
                {
                    zip = await Task.Run(() => updater.DownloadAsync(release, onProgress: progress));
                }
                catch (Exception e)
                {
                    RecordingLog.Write("startup update download failed: " + e.Message);
                }
                if (zip is not null) splash.SetText(Msg.Of("update-restarting", release.Tag).Render(language));
                splash.Close();
            };
            Application.Run(splash);
        }
        if (zip is null) return StartupUpdateNote.DownloadFailed;

        // A bridge helper (Lisp v0.61.x) waiting on this process must see it
        // start before we hand over, or it rolls this version back.
        StartupMarker.Write();
        var started = UpdateLauncher.ApplyAndRestart(zip, SingleInstance.ReleaseProcessClaim, RecordingLog.Write);
        if (started) Environment.Exit(0);
        // Rolled back: carry on with this build.
        if (!multiInstance) SingleInstance.ClaimForProcess();
        return StartupUpdateNote.DownloadFailed;
    }

    /// <summary>The small progress window (update-splash).</summary>
    private sealed class Splash : Form
    {
        private readonly Label _label;

        public Splash(string text)
        {
            Text = ClientHost.WindowTitle;
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(24);
            _label = new Label { Text = text, AutoSize = true, MinimumSize = new Size(360, 0), Font = new Font("Segoe UI", 10f) };
            Controls.Add(_label);
        }

        public void SetText(string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(() => _label.Text = text);
            else _label.Text = text;
        }
    }
}

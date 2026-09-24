using RappyRuns.Core;
using RappyRuns.Core.Update;

namespace RappyRuns.Host.Ipc;

/// <summary><c>updates.check</c>: the Settings button (check-for-updates, gui.lisp:1616).</summary>
internal sealed class UpdateMethods(ClientHost host)
{
    public void Register(IIpcRegistry r) => r.Register("updates.check", _ => CheckAsync());

    /// <summary>
    /// Every outcome but a restart answers with a notice. A newer release
    /// downloads without asking; while a run or recording is in flight it is
    /// parked and applied at the first idle poll frame.
    /// </summary>
    private async Task<object?> CheckAsync()
    {
        var updater = host.Updater;
        if (ClientVersion.Current is null) return Notice.Info("update-dev-build", updater.ReleasePageUrl);
        if (host.Ui.Updating) return null;
        host.Ui.SetUpdating(true);
        try
        {
            host.SetVersionNote(Msg.Of("update-checking"));
            var release = await updater.FetchLatestReleaseAsync(host.ShutdownToken).ConfigureAwait(false);
            if (release is null)
            {
                host.SetVersionNote(Msg.Of("update-check-failed"));
                return Notice.Fail("update-check-failed-dialog");
            }
            if (!updater.IsNewer(release))
            {
                host.SetVersionNote(Msg.Of("update-up-to-date"));
                return Notice.Info("update-latest-dialog", ClientVersion.Display);
            }
            // run-update-download (gui.lisp:1671).
            if (!updater.InstallDirWritable()) return host.OfferManualDownload(emit: false);
            var progress = SelfUpdater.MegabyteProgress((mb, total) =>
                host.SetVersionNote(Msg.Of("update-downloading", release.Tag, mb, total)));
            var zip = await updater.DownloadAsync(release, onProgress: progress, cancellationToken: host.ShutdownToken)
                .ConfigureAwait(false);
            if (zip is null)
            {
                host.SetVersionNote(Msg.Of("update-download-failed"), error: true);
                return Notice.Fail("update-download-failed-dialog");
            }
            if (!updater.Deferred.OfferOrDefer(zip, release.Tag))
            {
                // Never swap the exe mid-run; the poll loop re-offers once idle.
                host.SetVersionNote(Msg.Of("update-after-run", release.Tag));
                return null;
            }
            host.ApplyUpdateRestart(zip, release.Tag);
            return null;
        }
        finally
        {
            host.Ui.SetUpdating(false);
        }
    }
}

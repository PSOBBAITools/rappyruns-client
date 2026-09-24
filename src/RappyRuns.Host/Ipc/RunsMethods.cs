using System.Diagnostics;
using System.Text.Json;
using RappyRuns.Core.Config;
using RappyRuns.Core.Media;
using RappyRuns.Core.Store;

namespace RappyRuns.Host.Ipc;

/// <summary><c>runs.*</c>: the Runs tab buttons (ui-shell §1.2.3).</summary>
internal sealed class RunsMethods(ClientHost host)
{
    private const string YouTubeUpload = "https://www.youtube.com/upload";

    public void Register(IIpcRegistry r)
    {
        r.RegisterSync("runs.open", p =>
        {
            if (Find(P.Str(p, "id"))?.Url is { } url) ClientHost.OpenExternal(url);
            return null;
        });
        r.RegisterSync("runs.uploadVideo", UploadVideo);
        r.RegisterSync("runs.openRecordingsFolder", _ =>
        {
            OpenRecordingsFolder();
            return null;
        });
        r.RegisterSync("runs.openMyRuns", _ =>
        {
            ClientHost.OpenExternal(ConfigStore.ApiUrl(host.Config.ServerUrl, "/my/runs"));
            return null;
        });
        r.RegisterSync("runs.retry", _ =>
        {
            // The poll loop owns submission; works unlinked too (anonymous guest).
            // A manual retry also restarts any video upload's failure streak (S17).
            host.Queue.ResetUploadFailures();
            host.Poll.RequestRetry();
            return null;
        });
        r.RegisterSync("runs.clear", _ =>
        {
            host.Queue.Clear();
            return null;
        });
    }

    private RunEntry? Find(string? id) =>
        Guid.TryParse(id, out var guid) ? host.Queue.Find(guid) : null;

    /// <summary>
    /// upload-video-callback (gui.lisp:713): the row's recording, or the newest
    /// run a YouTube link can still be attached to; Explorer with the file
    /// selected plus the YouTube upload page.
    /// </summary>
    private object? UploadVideo(JsonElement p)
    {
        var id = P.Str(p, "id");
        var selected = id is null ? null : Find(id);
        if (selected is not null && selected.VideoPath is null) return Notice.Info("no-recording-for-run");
        var entry = selected ?? host.Queue.Entries.FirstOrDefault(e =>
            e.VideoPath is not null && (!e.Is(RunKeys.VideoAttached) || RunEntries.HostedVideoReplaceable(e.Data)));
        if (entry?.VideoPath is not { } path) return Notice.Info("no-recordings-yet");
        if (!RecordingFiles.PathExists(path)) return Notice.Info("recording-file-missing", path);
        // Explorer only accepts backslashes and a quoted path after /select, (R23).
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path.Replace('/', '\\')}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            RecordingLog.Write("explorer failed: " + e.Message);
        }
        ClientHost.OpenExternal(YouTubeUpload);
        return null;
    }

    /// <summary>open-recordings-folder-callback: created on demand so it works before the first recording.</summary>
    private void OpenRecordingsFolder()
    {
        try
        {
            var dir = host.RecordDir();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            RecordingLog.Write("open recordings folder failed: " + e.Message);
        }
    }
}

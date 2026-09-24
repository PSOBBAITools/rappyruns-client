using System.Text.Json;
using RappyRuns.Core.Api;
using RappyRuns.Core.Config;
using RappyRuns.Core.Ghost;
using RappyRuns.Core.Media;

namespace RappyRuns.Host.Ipc;

/// <summary><c>settings.*</c>: save-and-apply toggles and the connection form (ipc.md, ui-shell §1.4).</summary>
internal sealed class SettingsMethods(ClientHost host)
{
    private ConfigStore Config => host.Config;

    public void Register(IIpcRegistry r)
    {
        r.RegisterSync("settings.set", Set);
        r.Register("settings.saveConnection", SaveConnectionAsync);
        r.Register("settings.setAutoPublish", SetAutoPublishAsync);
        r.RegisterSync("settings.setAutostart", p => new { enabled = host.SetAutostart(P.Bool(p, "enabled")) });
        r.RegisterSync("settings.chooseRecordDir", _ => ChooseRecordDir());
        r.RegisterSync("settings.setTriggerLog", SetTriggerLog);
    }

    /// <summary>The plain toggles: each applies and saves itself (no Save button).</summary>
    private object? Set(JsonElement p)
    {
        var key = P.Str(p, "key") ?? throw new ArgumentException("settings.set: no key");
        var on = P.Bool(p, "value");
        switch (key)
        {
            case "trackingOnly": Config.TrackingOnly = on; break;
            case "trackingPrivate": Config.TrackingPrivate = on; break;
            case "recordAudio": Config.RecordAudio = on; break;
            case "ghostRace": Config.GhostRace = on; break;
            case "ghostOverlay":
                Config.GhostOverlay = on;
                // Off hides at once; on shows with the next 4 Hz update.
                if (!on) ClientHost.Try("overlay", host.Overlay.Hide);
                break;
            case "ghostMarker": Config.GhostMarker = on; break;
            case "overlayCorner":
                // Unknown values place top-right, like the Lisp CASE default.
                Config.OverlayCorner = OverlayPlacement.ParseCorner(P.Str(p, "value")).KeywordName().ToUpperInvariant();
                break;
            case "autoUpdate": Config.AutoUpdate = on; break;
            case "closeToTray": Config.CloseToTray = on; break;
            case "startMinimized": Config.StartMinimized = on; break;
            case "rankToast": Config.RankToast = on; break;
            default: throw new ArgumentException($"settings.set: unknown key {key}");
        }
        host.SaveConfig();
        return host.Ui.Settings;
    }

    /// <summary>
    /// save-settings-callback (gui.lisp:596): trim and save the URL (debug
    /// only) and token, re-check the server, and await the token check for the
    /// dialog (token-ok / rejected; nothing for a blank token or a network failure).
    /// </summary>
    private async Task<object?> SaveConnectionAsync(JsonElement p)
    {
        if (P.Str(p, "serverUrl") is { } url) Config.ServerUrl = ConfigStore.TrimServerUrl(url);
        Config.ApiToken = Tokens.Normalize(P.Str(p, "apiToken"));
        host.SaveConfig();
        _ = host.CheckServerAsync();
        // Null: a later save or pairing superseded this check; no dialog for it.
        var result = await host.CheckTokenAsync().ConfigureAwait(false);
        return result?.Kind switch
        {
            TokenCheckKind.Ok => Notice.Info("token-ok-dialog", result.User?.Username ?? ""),
            TokenCheckKind.Unauthorized => Notice.Fail("token-rejected-dialog"),
            _ => null,
        };
    }

    /// <summary>
    /// toggle-auto-publish-callback (gui.lisp:1445): a server-side setting. The
    /// UI already confirmed turning it on. On failure the last known server value
    /// comes back with the reason (R18).
    /// </summary>
    private async Task<object?> SetAutoPublishAsync(JsonElement p)
    {
        var enabled = P.Bool(p, "enabled");
        try
        {
            await host.Api.UpdateAutoPublishAsync(enabled, cancellationToken: host.ShutdownToken).ConfigureAwait(false);
            Config.AutoPublish = enabled;
            host.SaveConfig();
            return new { enabled, notice = (Notice?)null };
        }
        catch (Exception e) when (e is ApiException or OperationCanceledException)
        {
            host.Ui.InvalidateSettings();
            return new { enabled = Config.AutoPublish, notice = (Notice?)Notice.Fail("auto-publish-failed", e.Message) };
        }
    }

    /// <summary>choose-record-dir-callback (gui.lisp:660): the system folder picker; saved at once.</summary>
    private object? ChooseRecordDir()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Msg.Of("choose-record-dir").Render(host.Language),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        try
        {
            dialog.InitialDirectory = host.RecordDir();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Start wherever Windows likes.
        }
        var owner = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
        if (dialog.ShowDialog(owner) != DialogResult.OK || string.IsNullOrEmpty(dialog.SelectedPath)) return null;
        Config.RecordDir = dialog.SelectedPath;
        host.SaveConfig();
        return new { recordDir = RecordingFiles.ResolveRecordDir(Config.RecordDir) };
    }

    /// <summary>toggle-trigger-log-callback (gui.lisp:876): on starts the file at once and says where.</summary>
    private object? SetTriggerLog(JsonElement p)
    {
        var on = P.Bool(p, "enabled");
        Config.TriggerLog = on;
        host.SaveConfig();
        if (!on)
        {
            host.TriggerLog.Close();
            return null;
        }
        return Notice.Info("trigger-log-on", host.TriggerLog.Start());
    }
}

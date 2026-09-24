using System.Text.Json;
using RappyRuns.Core.Api;
using RappyRuns.Core.PinShare;

namespace RappyRuns.Host.Ipc;

/// <summary><c>pinshare.*</c>: the relay toggle, the passphrase and pin-set saves (ui-shell §1.4.5).</summary>
internal sealed class PinShareMethods(ClientHost host)
{
    public void Register(IIpcRegistry r)
    {
        r.RegisterSync("pinshare.setEnabled", p =>
        {
            // toggle-pinshare-callback: consent to write the addon; the relay reacts within a tick.
            host.Config.PinshareEnabled = P.Bool(p, "enabled");
            host.Config.PinshareChannel = Clean(P.Str(p, "channel"));
            host.SaveConfig();
            return null;
        });
        r.RegisterSync("pinshare.applyChannel", p =>
        {
            host.Config.PinshareChannel = Clean(P.Str(p, "channel"));
            host.SaveConfig();
            return null;
        });
        r.Register("pinshare.saveNew", _ => SaveAsync(overwrite: false));
        r.RegisterSync("pinshare.overwriteCheck", _ => OverwriteCheck());
        r.Register("pinshare.overwrite", _ => SaveAsync(overwrite: true));
    }

    /// <summary>The passphrase as the relay uses it: control characters out, trimmed, at most 64 characters.</summary>
    private static string Clean(string? channel) => PinShareSettings.Channel(channel);

    private object? OverwriteCheck()
    {
        var set = host.PinSets.Current;
        var items = host.PinShare.ChannelItems;
        var block = PinSetSave.Precheck(host.PinSets.QuestSlugs, items, overwrite: true, inUse: set);
        if (block != PinSetSaveBlock.None) return new { ok = false, notice = new Notice(StatusMsgs.PinSetBlock(block)) };
        return new { ok = true, name = set!.DisplayName ?? "", pins = items!.Pins.Count, arrows = items.Arrows.Count };
    }

    /// <summary>
    /// save-pin-set-new-callback / -overwrite-callback (gui.lisp:1276-1299): the
    /// channel's current pins and arrows as a new private set on the loaded
    /// quest, or into the user's own set in use (the UI confirmed first).
    /// </summary>
    private async Task<object?> SaveAsync(bool overwrite)
    {
        var slugs = host.PinSets.QuestSlugs;
        var items = host.PinShare.ChannelItems;
        var set = overwrite ? host.PinSets.Current : null;
        var block = PinSetSave.Precheck(slugs, items, overwrite, set);
        if (block != PinSetSaveBlock.None) return new Notice(StatusMsgs.PinSetBlock(block));
        var body = PinSetSave.Body(slugs![0], items!.Pins, items.Arrows);
        try
        {
            var result = await host.Api.SavePinSetAsync(body, overwrite ? set!.Id : null, cancellationToken: host.ShutdownToken)
                .ConfigureAwait(false);
            // The locked copy must show the new pins.
            if (result.Outcome == RappyRuns.Core.Api.PinSetSaveOutcome.Updated) host.PinSets.Refetch();
            return StatusMsgs.PinSetReport(result);
        }
        catch (Exception e) when (e is ApiException or OperationCanceledException or JsonException)
        {
            return Notice.Fail("pinshare-save-failed", e.Message);
        }
    }
}

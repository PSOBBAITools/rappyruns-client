using System.Text.Json;
using System.Text.Json.Nodes;
using RappyRuns.Core.Api;
using RappyRuns.Core.Game;
using RappyRuns.Core.PinShare;
using RappyRuns.Core.Store;
using PinShareSaveOutcome = RappyRuns.Core.Api.PinSetSaveOutcome;

namespace RappyRuns.Host;

/// <summary>
/// The status texts the modules format for one language, as <see cref="Msg"/>s
/// the UI renders in its current one (ipc.md: the host never sends translated
/// text). Each mirrors the module function it names, key for key.
/// </summary>
public static class StatusMsgs
{
    private static Msg? HintMsg(ApiException error) =>
        (ErrorText.HintKey(error.Failure) ?? ErrorText.HintKey(ErrorText.WindowsErrorCode(error.Message))) is { } key
            ? Msg.Of(key)
            : null;

    /// <summary><see cref="ErrorText.ServerStatus"/>: the server line after a failed check.</summary>
    public static Msg ServerError(Exception error)
    {
        if (error is not ApiException api) return Msg.Of("server-check-failed", error.Message);
        if (HintMsg(api) is { } hint) return Msg.Of("server-error-prefix", hint);
        var message = api.Message;
        if (message.Contains("Bad URL", StringComparison.Ordinal)) return Msg.Of("server-bad-url");
        if (message.Contains("-> ", StringComparison.Ordinal)) return Msg.Of("server-unexpected", message);
        return Msg.Of("server-error-prefix", message);
    }

    /// <summary><see cref="ErrorText.TokenStatus"/>: /api/me could not be asked at all.</summary>
    public static Msg TokenError(Exception? error)
    {
        object detail = error switch
        {
            ApiException api => (object?)HintMsg(api) ?? api.Message,
            null => "unknown error",
            _ => error.Message,
        };
        return Msg.Of("token-could-not-verify", detail);
    }

    /// <summary><see cref="PsobbRejection.StatusLabel"/>.</summary>
    public static Msg Signature(PsobbRejection rejection) => rejection.Status switch
    {
        SignatureStatus.Unsigned => Msg.Of("signature-unsigned"),
        SignatureStatus.Valid => Msg.Of("signature-untrusted-signer", rejection.Signer ?? "?"),
        _ => Msg.Of("signature-invalid"),
    };

    /// <summary><see cref="PinShareStatus.Describe"/> with the ui-shell §1.4.5 tones.</summary>
    public static Line PinShare(PinShareStatus status) => status.Kind switch
    {
        PinShareStatusKind.Off => Line.Neutral(Msg.Of("pinshare-status-off")),
        PinShareStatusKind.NotAllowed => Line.Neutral(Msg.Of("pinshare-status-not-allowed")),
        PinShareStatusKind.NoChannel => Line.Neutral(Msg.Of("pinshare-status-no-channel")),
        PinShareStatusKind.WaitingGame => Line.Neutral(Msg.Of("pinshare-status-waiting-game")),
        PinShareStatusKind.Connecting => Line.Busy(Msg.Of("pinshare-status-connecting")),
        PinShareStatusKind.Connected => Line.Ok(Msg.Of("pinshare-status-connected", status.Text, status.Count)),
        PinShareStatusKind.ConnectedNoAddon => Line.Neutral(Msg.Of("pinshare-status-no-addon")),
        PinShareStatusKind.LocalOnly => Line.Neutral(Msg.Of("pinshare-status-local-only", status.Text)),
        PinShareStatusKind.Error => Line.Error(Msg.Of("pinshare-status-error", status.Text)),
        PinShareStatusKind.NoAddonPlugin => Line.Error(Msg.Of("pinshare-status-no-plugin")),
        PinShareStatusKind.InstallFailed => Line.Error(Msg.Of("pinshare-status-install-failed", status.Text)),
        PinShareStatusKind.BrokenLink => Line.Error(Msg.Of("pinshare-status-broken-link", status.Text)),
        PinShareStatusKind.Conflict => Line.Error(Msg.Of("pinshare-status-conflict")),
        _ => Line.Neutral(Msg.Of("pinshare-status-off")),
    };

    /// <summary><see cref="PinSetTracker.PinSetText"/> (gui.lisp:1229).</summary>
    public static Line PinSet(PinSet? current, IReadOnlyList<string>? questSlugs)
    {
        if (current is not null) return Line.Neutral(Msg.Of("pinshare-pin-set-active", current.DisplayName, current.Author));
        return Line.Neutral(Msg.Of(questSlugs is not null ? "pinshare-pin-set-none" : "pinshare-pin-set-none-idle"));
    }

    /// <summary><see cref="PinSetSave.BlockText"/>.</summary>
    public static Msg PinSetBlock(PinSetSaveBlock block) => block switch
    {
        PinSetSaveBlock.NoQuest => Msg.Of("pinshare-save-no-quest"),
        PinSetSaveBlock.NoItems => Msg.Of("pinshare-save-no-items"),
        _ => Msg.Of("pinshare-save-not-mine"),
    };

    /// <summary><see cref="PinSetSave.ReportText"/> over the API result (gui.lisp:1237).</summary>
    public static Notice PinSetReport(PinSetSaveResult result) => result.Outcome switch
    {
        PinShareSaveOutcome.Created => Notice.Info("pinshare-save-created", result.Url),
        PinShareSaveOutcome.Updated => Notice.Info("pinshare-save-updated", result.Pins, result.Arrows),
        _ => Notice.Fail("pinshare-save-failed", result.FailureText),
    };

    /// <summary>
    /// <see cref="RunDisplay.RunVideoLabel"/> as a Msg, so an upload's percent
    /// travels as a number the UI can draw a bar with.
    /// </summary>
    public static Msg? RunVideo(RunEntry entry, int? uploadPercent)
    {
        if (entry.Is(RunKeys.VideoUploaded)) return Msg.Of("video-uploaded");
        if (entry.Is(RunKeys.VideoAttached)) return Msg.Of("video-attached");
        if (uploadPercent is { } percent) return Msg.Of("video-uploading", percent);
        if (entry.Is(RunKeys.UploadGivenUp)) return Msg.Of("video-upload-failed");
        if (entry.Is(RunKeys.VideoPath)) return Msg.Of("video-saved");
        return null;
    }

    /// <summary>A trigger as the wire JSON the UI and POST /api/quests use.</summary>
    public static JsonNode? TriggerJson(Trigger? trigger) => trigger is null ? null : JsonNode.Parse(trigger.ToJson());

    /// <summary>A UI trigger object back to a module trigger; null when malformed.</summary>
    public static Trigger? ParseTrigger(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Object } e ? Trigger.FromJson(e) : null;
}

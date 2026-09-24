using System.Text.Json;
using System.Text.Json.Nodes;
using RappyRuns.Core.Api;
using RappyRuns.Core.Game;

namespace RappyRuns.Host.Ipc;

/// <summary><c>rules.*</c>: quest rule registration for moderators (ui-shell §3; the form logic is in the UI).</summary>
internal sealed class RulesMethods(ClientHost host)
{
    public void Register(IIpcRegistry r)
    {
        r.Register("rules.prepare", _ => PrepareAsync());
        r.Register("rules.create", CreateAsync);
    }

    /// <summary>run-quest-rule-flow's fetch half: the timeable quests, the detected parent and the room rows.</summary>
    private async Task<object?> PrepareAsync()
    {
        JsonElement quests;
        try
        {
            var array = await host.Api.FetchQuestsAsync(host.ShutdownToken).ConfigureAwait(false);
            quests = JsonSerializer.SerializeToElement(array);
        }
        catch (Exception e) when (e is ApiException or OperationCanceledException)
        {
            return new { ok = false, notice = Notice.Fail("rule-fetch-failed", e.Message) };
        }
        var parents = RuleForm.TimeableQuests(quests);
        if (parents.Count == 0) return new { ok = false, notice = Notice.Info("rule-no-parents") };
        var detected = RuleForm.DetectedParent(parents, host.RunLogs.RunQuest);
        return new
        {
            ok = true,
            parents = parents.Select(q => new { slug = Text(q, "slug"), name = Text(q, "name") }).ToList(),
            detected = detected is { } d ? Text(d, "slug") : null,
            rows = host.RunLogs.RoomRows().Select((row, i) => ClientHost.RoomDto(row, i)).ToList(),
        };
    }

    private static string Text(JsonElement quest, string key) =>
        quest.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    /// <summary>post-quest-rule-in-background (gui.lisp:922): POST /api/quests and word the outcome.</summary>
    private async Task<object?> CreateAsync(JsonElement p)
    {
        var parent = P.Str(p, "parent");
        var name = P.Str(p, "name")?.Trim(' ');
        var description = P.Str(p, "description")?.Trim(' ');
        var end = StatusMsgs.ParseTrigger(P.Get(p, "end"));
        var start = P.Get(p, "start") is { } s ? StatusMsgs.ParseTrigger(s) : null;
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(description) || end is null)
            return Notice.Fail("rule-need-values");
        try
        {
            var result = await host.Api.CreateQuestRuleAsync(parent, name, description,
                (JsonObject)StatusMsgs.TriggerJson(end)!, start is null ? null : (JsonObject)StatusMsgs.TriggerJson(start)!,
                cancellationToken: host.ShutdownToken).ConfigureAwait(false);
            var payload = result.Payload is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(result.Payload);
            switch (result.Outcome)
            {
                case QuestRuleOutcome.Created:
                    // Pull the new rule into the active detection defs at once.
                    _ = host.CheckServerAsync();
                    return Notice.Info("rule-created", ApiJson.GetString(result.Payload, "slug"));
                case QuestRuleOutcome.Duplicate:
                    return Notice.Info("rule-duplicate", RuleForm.RuleErrorMessage(payload));
                case QuestRuleOutcome.Forbidden:
                    return Notice.Info("rule-forbidden");
                default:
                    return Notice.Info("rule-rejected", RuleForm.RuleErrorMessage(payload));
            }
        }
        catch (Exception e) when (e is ApiException or OperationCanceledException)
        {
            return Notice.Fail("rule-post-failed", e.Message);
        }
    }
}

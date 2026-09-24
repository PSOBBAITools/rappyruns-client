using System.Text.Json;
using RappyRuns.Core.Api;
using RappyRuns.Core.I18n;

namespace RappyRuns.Host.Ipc;

/// <summary>Reading request params leniently: a missing or mistyped field reads as the default.</summary>
internal static class P
{
    public static JsonElement? Get(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public static string? Str(JsonElement p, string name) =>
        Get(p, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public static bool Bool(JsonElement p, string name) =>
        Get(p, name) is { ValueKind: JsonValueKind.True };

    public static Task<object?> Done(object? value = null) => Task.FromResult(value);

    public static void RegisterSync(this IIpcRegistry registry, string method, Func<JsonElement, object?> handler) =>
        registry.Register(method, p => Task.FromResult(handler(p)));
}

/// <summary><c>app.*</c>: the handshake, the language, external links (ipc.md).</summary>
internal sealed class AppMethods(ClientHost host)
{
    public void Register(IIpcRegistry r)
    {
        // Ordered: the snapshot must not be overtaken by (or overtake) the
        // runs/rooms/state events emitted around it.
        r.RegisterOrdered("app.hello", (_, reply) => host.Hello(reply));
        r.RegisterOrdered("app.setLanguage", SetLanguage);
        r.RegisterSync("app.openExternal", p =>
        {
            if (P.Str(p, "url") is { } url && Urls.IsValidHttpUrl(url)) ClientHost.OpenExternal(url);
            return null;
        });
    }

    /// <summary>
    /// Save <c>:language</c>, retitle the tray, and hand the UI the new strings.
    /// No window rebuild (ui-shell §1.4.1); the runs list is re-sent because its
    /// status column is formatted by the store module in one language.
    /// </summary>
    private void SetLanguage(JsonElement p, Action<object?> reply)
    {
        var language = Languages.FromCode(P.Str(p, "language"));
        if (language != host.Config.Language)
        {
            host.Config.Language = language;
            host.SaveConfig();
            host.Shell.OnLanguageChanged();
        }
        host.PublishRuns(force: true);
        host.Snapshot(handshake: false, snapshot => reply(snapshot));
    }
}

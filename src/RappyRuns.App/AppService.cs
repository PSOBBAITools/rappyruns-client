using System.Text.Json;
using RappyRuns.App.Host;
using RappyRuns.Core;
using RappyRuns.Core.I18n;

namespace RappyRuns.App;

/// <summary>
/// IPC methods about the app itself: the handshake, language, external links.
/// </summary>
internal sealed class AppService
{
    private readonly CommandLine _options;
    // P0 keeps the language in memory; P1 loads and saves it with config.sexp.
    private Language _language = Language.En;

    public AppService(IpcHost ipc, CommandLine options)
    {
        _options = options;
        ipc.Register("app.hello", _ => Hello());
        ipc.Register("app.setLanguage", SetLanguage);
        ipc.Register("app.openExternal", p =>
        {
            MainForm.OpenExternally(p.GetProperty("url").GetString() ?? "");
            return (object?)null;
        });
    }

    private object Hello()
    {
        // The UI answered, so the new exe demonstrably runs: tell the bridge
        // updater it can keep this version.
        StartupMarker.Write();
        return Snapshot();
    }

    private object SetLanguage(JsonElement p)
    {
        _language = Languages.FromCode(p.GetProperty("language").GetString());
        return Snapshot();
    }

    private object Snapshot() => new
    {
        version = ClientVersion.Display,
        debug = _options.Debug,
        language = _language.Code(),
        languages = Languages.All.Select(l => new { code = l.Code(), label = l.Label() }),
        strings = Strings.Default.TemplatesFor(_language),
    };
}

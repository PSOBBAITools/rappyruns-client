using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RappyRuns.Core.I18n;

namespace RappyRuns.Host;

/// <summary>
/// The JSON settings of the host &lt;-&gt; UI contract (docs/ipc.md): camelCase
/// (<see cref="JsonSerializerDefaults.Web"/>) and nulls sent explicitly.
/// </summary>
public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static JsonNode? ToNode(object? value) => JsonSerializer.SerializeToNode(value, Options);

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>Where host events go (the WebView2 IPC in the app, a recorder in tests). Any thread.</summary>
public interface IUiSink
{
    void Emit(string name, object? data);
}

/// <summary>A UI -> host method: gets the request's <c>params</c>, returns the <c>result</c>.</summary>
public delegate Task<object?> IpcHandler(JsonElement parameters);

/// <summary>
/// A UI -> host method whose response must stay in order with the events
/// (<c>app.hello</c>: a runs list or state patch emitted before the snapshot
/// was taken must not arrive after it). The handler calls
/// <paramref name="reply"/> exactly once before it returns, typically while
/// holding the lock its events are emitted under; the response joins the
/// event queue at that moment.
/// </summary>
public delegate void IpcOrderedHandler(JsonElement parameters, Action<object?> reply);

/// <summary>The method table the IPC transport dispatches into.</summary>
public interface IIpcRegistry
{
    void Register(string method, IpcHandler handler);

    void RegisterOrdered(string method, IpcOrderedHandler handler);
}

/// <summary>
/// Localizable text (ipc.md "Msg"): an i18n key with arguments, rendered by the
/// UI in its current language, or raw text that is not localized. Arguments
/// are strings, numbers, null or nested Msgs.
/// </summary>
[JsonConverter(typeof(MsgJsonConverter))]
public sealed class Msg
{
    private Msg(string? key, IReadOnlyList<object?> args, string? text)
    {
        Key = key;
        Args = args;
        Text = text;
    }

    public string? Key { get; }

    public IReadOnlyList<object?> Args { get; }

    public string? Text { get; }

    /// <summary>A strings.json key with its template arguments.</summary>
    public static Msg Of(string key, params object?[] args) => new(key, args, null);

    /// <summary>Text shown as is (server messages, paths, texts the store module formats).</summary>
    public static Msg Raw(string text) => new(null, [], text);

    /// <summary>The text in <paramref name="language"/> (tray balloons, logs, tests).</summary>
    public string Render(Language language) =>
        Text ?? Strings.Default.Tr(language, Key!, Args.Select(a => a is Msg m ? m.Render(language) : a).ToArray());

    public override string ToString() => IpcJson.Serialize(this);
}

internal sealed class MsgJsonConverter : JsonConverter<Msg>
{
    public override Msg Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Msg is host -> UI only");

    public override void Write(Utf8JsonWriter writer, Msg value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Text is not null)
        {
            writer.WriteString("text", value.Text);
        }
        else
        {
            writer.WriteString("key", value.Key);
            writer.WriteStartArray("args");
            foreach (var arg in value.Args) WriteArg(writer, arg, options);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private void WriteArg(Utf8JsonWriter writer, object? arg, JsonSerializerOptions options)
    {
        switch (arg)
        {
            case null:
                writer.WriteNullValue();
                break;
            case Msg msg:
                Write(writer, msg, options);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case int or long or short or byte or uint:
                writer.WriteNumberValue(Convert.ToInt64(arg, CultureInfo.InvariantCulture));
                break;
            case float or double or decimal:
                writer.WriteNumberValue(Convert.ToDouble(arg, CultureInfo.InvariantCulture));
                break;
            default:
                writer.WriteStringValue(Convert.ToString(arg, CultureInfo.InvariantCulture));
                break;
        }
    }
}

/// <summary>Status-line colors (ipc.md "Line"): 'error' is the Lisp client's red text.</summary>
public static class Tone
{
    public const string Neutral = "neutral";
    public const string Ok = "ok";
    public const string Busy = "busy";
    public const string Error = "error";
}

/// <summary>A status line.</summary>
public sealed record Line(Msg Msg, string Tone = Host.Tone.Neutral)
{
    public static Line Neutral(Msg msg) => new(msg);

    public static Line Ok(Msg msg) => new(msg, Host.Tone.Ok);

    public static Line Busy(Msg msg) => new(msg, Host.Tone.Busy);

    public static Line Error(Msg msg) => new(msg, Host.Tone.Error);
}

/// <summary>A message box (ipc.md "Notice"). With <see cref="ConfirmUrl"/> it is a Yes/No question.</summary>
public sealed record Notice(Msg Message, bool Error = false, string? ConfirmUrl = null)
{
    public static Notice Info(string key, params object?[] args) => new(Msg.Of(key, args));

    public static Notice Fail(string key, params object?[] args) => new(Msg.Of(key, args), Error: true);
}

/// <summary>The Settings tab (ipc.md "Settings").</summary>
public sealed record SettingsDto
{
    public required string Language { get; init; }
    public required string ServerUrl { get; init; }
    public required string ApiToken { get; init; }
    public bool TrackingOnly { get; init; }
    public bool TrackingPrivate { get; init; }
    public bool RecordAudio { get; init; }
    public double RecordMaxTotalGb { get; init; }
    public bool AutoPublish { get; init; }
    public required string RecordDir { get; init; }
    public bool GhostRace { get; init; }
    public bool GhostOverlay { get; init; }
    public bool GhostMarker { get; init; }
    public required string OverlayCorner { get; init; }
    public bool PinshareEnabled { get; init; }
    public required string PinshareChannel { get; init; }
    public bool AutoUpdate { get; init; }
    public bool CloseToTray { get; init; }
    public bool Autostart { get; init; }
    public bool StartMinimized { get; init; }
    public bool RankToast { get; init; }
    public bool TriggerLog { get; init; }
}

/// <summary>One Runs-list row (ipc.md "RunRow").</summary>
public sealed record RunRowDto(
    string Id,
    string Quest,
    long TimeMs,
    long Players,
    bool Pb,
    Msg? Video,
    IReadOnlyList<Msg> Status,
    bool StatusError,
    string? Url,
    bool HasRecording);

/// <summary>One Rooms-list row (ipc.md "RoomRow"): kind "clear" or "enemy".</summary>
public sealed record RoomRowDto(string Id, string Area, string Kind, string? Name, JsonNode? Trigger);

/// <summary>A language choice for the Settings radio (always labelled in its own language).</summary>
public sealed record LanguageDto(string Code, string Label);

/// <summary>The <c>app.hello</c> / <c>app.setLanguage</c> result (ipc.md "AppSnapshot").</summary>
public sealed record AppSnapshotDto(
    string Version,
    bool Debug,
    string Language,
    IReadOnlyList<LanguageDto> Languages,
    IReadOnlyDictionary<string, string> Strings,
    JsonObject State,
    IReadOnlyList<RunRowDto> Runs,
    IReadOnlyList<RoomRowDto> Rooms);

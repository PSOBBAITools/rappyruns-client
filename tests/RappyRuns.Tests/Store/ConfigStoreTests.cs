using RappyRuns.Core.Config;
using RappyRuns.Core.I18n;
using RappyRuns.Core.Sexp;
using static RappyRuns.Tests.Store.StoreTestSupport;

namespace RappyRuns.Tests.Store;

/// <summary>config.lisp: tests-helpers.lisp run-config-migration-tests and the config parts of run-ux-helper-tests.</summary>
public class ConfigStoreTests
{
    /// <summary>with-recording-config: an in-memory config with overrides laid over the defaults.</summary>
    private static ConfigStore WithConfig(string overrides = "()", params string[] args)
    {
        var store = new ConfigStore(Path.Combine(Path.GetTempPath(), "rr-unused-" + Guid.NewGuid().ToString("N")), args);
        var plist = P(overrides);
        foreach (var key in plist.Keys.Reverse()) store.Set(key, plist.Get(key)!);
        return store;
    }

    // ---- run-config-migration-tests ----

    [Fact]
    public void OtherKeysSurviveTheMigration()
    {
        var migrated = ConfigStore.Migrate(P("(:server-url \"http://localhost:8080\" :api-token \"eta_x\")"));
        Assert.Equal("http://localhost:8080", migrated.Get("server-url")!.AsString);
        Assert.Equal("eta_x", migrated.Get("api-token")!.AsString);
    }

    [Fact]
    public void TheDroppedTokenPromptShownKeyIsScrubbed()
    {
        var migrated = ConfigStore.Migrate(P("(:token-prompt-shown t :record-audio t)"));
        Assert.False(migrated.Contains("token-prompt-shown"));
        Assert.True(migrated.Get("record-audio")!.IsTrue);
    }

    [Fact]
    public void ForcedConfigKeysAreScrubbedSoTheDefaultWins()
    {
        var migrated = ConfigStore.Migrate(P("(:completion-sound t :video-upload nil :auto-submit nil)"));
        Assert.All(ConfigKeys.Forced, key => Assert.False(migrated.Contains(key)));
    }

    [Theory]
    [InlineData("a fresh install uses the new recordings folder", false, false, RecordDirChoice.UseNew)]
    [InlineData("only the pre-rename folder present triggers the migration", true, false, RecordDirChoice.Migrate)]
    [InlineData("an already-migrated install stays on the new folder", false, true, RecordDirChoice.UseNew)]
    [InlineData("both folders present never renames onto the existing one", true, true, RecordDirChoice.UseNew)]
    public void DefaultRecordDirChoice(string label, bool oldExists, bool newExists, RecordDirChoice expected)
    {
        Assert.True(expected == RecordDirectory.Choose(oldExists, newExists), label);
    }

    [Fact]
    public void RecordDirResolveRenamesThePreRenameFolder()
    {
        using var home = new TempDir("rr-store-test");
        var old = Path.Combine(home.Path, "Videos", "EphineaTA");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "a.mp4"), "x");
        var resolved = RecordDirectory.Resolve("", home.Path);
        Assert.Equal(Path.Combine(home.Path, "Videos", "RappyRuns"), resolved);
        Assert.True(File.Exists(Path.Combine(resolved, "a.mp4")));
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void RecordDirResolvePrefersTheConfiguredFolderTrimmed()
    {
        Assert.Equal(@"D:\rec", RecordDirectory.Resolve(@"  D:\rec ", @"C:\nowhere"));
    }

    // ---- config parts of run-ux-helper-tests ----

    [Fact]
    public void UnlinkedIsTrueWithTheDefaultEmptyApiToken() =>
        Assert.True(WithConfig().IsUnlinked, "unlinked-p is true with the default (empty) api token");

    [Fact]
    public void UnlinkedIsFalseOnceALinkedTokenIsConfigured() =>
        Assert.False(WithConfig("(:api-token \"eta_x\")").IsUnlinked, "unlinked-p is false once a linked token is configured");

    [Fact]
    public void UnlinkedIgnoresTheGuestToken() =>
        Assert.True(WithConfig("(:anon-token \"eta_g\")").IsUnlinked, "unlinked-p ignores the guest token - a guest is not linked");

    [Fact]
    public void SubmissionTokenPrefersTheLinkedTokenOverTheGuest() =>
        Assert.Equal("eta_x", WithConfig("(:api-token \"eta_x\" :anon-token \"eta_g\")").SubmissionToken);

    [Fact]
    public void SubmissionTokenFallsBackToTheGuestToken() =>
        Assert.Equal("eta_g", WithConfig("(:anon-token \"eta_g\")").SubmissionToken);

    [Fact]
    public void DebugModeIsOffByDefault() => Assert.False(WithConfig().DebugMode, "debug mode is off by default");

    [Fact]
    public void DebugModeCanBeEnabledInConfig() => Assert.True(WithConfig("(:debug t)").DebugMode, "debug mode can be enabled in config");

    [Fact]
    public void DebugModeFollowsTheCommandLineFlagCaseInsensitively() =>
        Assert.True(WithConfig("()", "--DEBUG").DebugMode);

    [Fact]
    public void StartupMinimizedFollowsConfigOrFlag()
    {
        Assert.False(WithConfig().StartupMinimized);
        Assert.True(WithConfig("(:start-minimized t)").StartupMinimized);
        Assert.True(WithConfig("()", "--minimized").StartupMinimized);
        Assert.False(WithConfig("()", "--minimizedx").StartupMinimized);
    }

    [Theory]
    [InlineData("normalize-token trims spaces and CRLF", "  eta_abc123\r\n", "eta_abc123")]
    [InlineData("normalize-token trims tabs", "\teta_abc123\t", "eta_abc123")]
    [InlineData("normalize-token maps nil to empty", null, "")]
    [InlineData("normalize-token keeps empty empty", "   ", "")]
    public void NormalizeToken(string label, string? input, string expected) =>
        Assert.True(expected == ConfigStore.NormalizeToken(input), label);

    // ---- config-value semantics and the file ----

    [Fact]
    public void APresentNilWinsOverTheDefault()
    {
        var config = WithConfig("(:hw-encode nil)");
        Assert.False(config.HwEncode);
        Assert.True(WithConfig().HwEncode);
    }

    [Fact]
    public void AbsentKeysFallBackToTheDefaultOrNil()
    {
        var config = WithConfig();
        Assert.Equal(ConfigKeys.DefaultServerUrl, config.ServerUrl);
        Assert.Equal(20, config.RecordMaxTotalGb);
        Assert.True(config.Get("no-such-key").IsNil);
    }

    [Fact]
    public void AFirstRunWritesEveryDefaultExceptTheForcedKeys()
    {
        using var dir = new TempDir("rr-store-test");
        var config = ConfigStore.Open(dir.Path);
        config.Save();
        var saved = Plist.From(SexpReader.TryReadFile(config.FilePath))!;
        var expected = ConfigKeys.Defaults.Select(kv => kv.Key).Where(k => !ConfigKeys.Forced.Contains(k)).ToList();
        Assert.Equal(expected, saved.Keys.ToList());
    }

    [Fact]
    public void ForcedKeysInTheFileNeverOverrideTheDefault()
    {
        using var dir = new TempDir("rr-store-test");
        File.WriteAllText(Path.Combine(dir.Path, "config.sexp"), "(:VIDEO-UPLOAD COMMON-LISP:NIL :AUTO-SUBMIT COMMON-LISP:NIL :COMPLETION-SOUND COMMON-LISP:T)");
        var config = ConfigStore.Open(dir.Path);
        Assert.True(config.VideoUpload);
        Assert.True(config.AutoSubmit);
        Assert.False(config.CompletionSound);
        Assert.True(config.RecordEnabled);
        Assert.True(config.SubmitAborted);
    }

    [Fact]
    public void ACorruptFileFallsBackToDefaultsAndIsKeptAside()
    {
        using var dir = new TempDir("rr-store-test");
        var path = Path.Combine(dir.Path, "config.sexp");
        File.WriteAllText(path, "(:API-TOKEN \"unterminated");
        var config = ConfigStore.Open(dir.Path);
        Assert.Equal("", config.ApiToken);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void TypedAccessorsRoundTripThroughTheFile()
    {
        using var dir = new TempDir("rr-store-test");
        var config = ConfigStore.Open(dir.Path);
        config.Language = Language.Ja;
        config.OverlayCorner = "CUSTOM";
        config.OverlayPosition = (0.25f, 0.7894558f);
        config.SetRecordMaxTotalGb(2.5);
        config.PinshareChannel = "チャンネル";
        config.Save();

        var again = ConfigStore.Open(dir.Path);
        Assert.Equal(Language.Ja, again.Language);
        Assert.Equal("CUSTOM", again.OverlayCorner);
        Assert.Equal((0.25f, 0.7894558f), again.OverlayPosition);
        Assert.Equal(2.5, again.RecordMaxTotalGb);
        Assert.Equal("チャンネル", again.PinshareChannel);
    }

    [Fact]
    public void LanguageIsEnglishUnlessJa()
    {
        Assert.Equal(Language.En, WithConfig("(:language :fr)").Language);
        Assert.Equal(Language.En, WithConfig("(:language \"ja\")").Language);
        Assert.Equal(Language.Ja, WithConfig("(:language :ja)").Language);
    }

    [Fact]
    public void OverlayPositionNeedsTwoNumbers()
    {
        Assert.Null(WithConfig().OverlayPosition);
        Assert.Null(WithConfig("(:overlay-position (0.5 . 0.5))").OverlayPosition);
        Assert.Null(WithConfig("(:overlay-position (0.5 \"x\"))").OverlayPosition);
        Assert.Equal((1f, 0f), WithConfig("(:overlay-position (1 0))").OverlayPosition);
    }

    [Theory]
    [InlineData("(:record-max-total-gb 20)", 21474836480L)]
    [InlineData("(:record-max-total-gb 0)", null)]
    [InlineData("(:record-max-total-gb nil)", null)]
    [InlineData("(:record-max-total-gb \"\")", null)]
    [InlineData("(:record-max-total-gb 0.5)", 536870912L)]
    [InlineData("(:record-max-total-gb -3)", null)]
    public void RecordMaxTotalBytes(string config, long? expected) =>
        Assert.Equal(expected, WithConfig(config).RecordMaxTotalBytes);

    [Fact]
    public void UpdateRepoOverride()
    {
        Assert.Equal(ConfigStore.DefaultUpdateRepo, WithConfig().ResolveUpdateRepo());
        Assert.Equal(ConfigStore.DefaultUpdateRepo, WithConfig("(:update-repo \"\")").ResolveUpdateRepo());
        Assert.Equal("me/fork", WithConfig("(:update-repo \"me/fork\")").ResolveUpdateRepo());
    }

    [Fact]
    public void ServerUrlTrimming()
    {
        Assert.Equal("https://x", ConfigStore.TrimServerUrl("https://x/ / "));
        Assert.Equal("https://x/api/me", ConfigStore.ApiUrl("https://x//", "/api/me"));
    }

    [Fact]
    public void ChangedFiresWithTheKey()
    {
        var config = WithConfig();
        string? seen = "none";
        config.Changed += (_, e) => seen = e.Key;
        config.ApiToken = "t";
        Assert.Equal("API-TOKEN", seen);
    }

    [Fact]
    public void DefaultDirectoryIsUnderAppData()
    {
        Assert.EndsWith(ConfigStore.FolderName, ConfigStore.DefaultDirectory());
    }
}

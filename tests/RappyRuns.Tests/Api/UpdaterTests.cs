using System.IO.Compression;
using System.Text;
using System.Text.Json;
using RappyRuns.Core.Api;
using RappyRuns.Core.Update;

namespace RappyRuns.Tests.Api;

/// <summary>
/// Self-update (tests-helpers.lisp run-updater-tests, spec core §10). The Lisp
/// helper-script checks are ported to the in-process installer that replaces it.
/// </summary>
public class UpdaterTests : IDisposable
{
    private const string ReleaseSample = """
        {"tag_name": "v0.6.0",
         "prerelease": false,
         "assets": [
           {"name": "notes.txt", "size": 12, "browser_download_url": "https://example.com/notes.txt"},
           {"name": "OtherTool.zip", "size": 12345678,
            "browser_download_url": "https://github.com/x/y/releases/download/v0.6.0/OtherTool.zip"},
           {"name": "RappyRunsClient.zip", "size": 12345678,
            "browser_download_url": "https://github.com/x/y/releases/download/v0.6.0/RappyRunsClient.zip"}]}
        """;

    private readonly TempDir _dir = new("rr-update");

    public void Dispose() => _dir.Dispose();

    private string P(params string[] parts) => _dir.File(parts);

    // --- constants -----------------------------------------------------------------

    [Fact(DisplayName = "update constants are the contract values")]
    public void Constants()
    {
        Assert.Equal("PSOBBAITools/rappyruns-client", UpdateConstants.DefaultRepo);
        Assert.Equal("RappyRunsClient.zip", UpdateConstants.AssetName);
        Assert.Equal("RappyRunsClient.exe", UpdateConstants.ExeName);
        Assert.Equal("https://api.github.com/repos/PSOBBAITools/rappyruns-client/releases/latest",
            UpdateConstants.LatestReleaseApiUrl(UpdateConstants.DefaultRepo));
        Assert.Equal("https://github.com/a/b/releases/latest", UpdateConstants.ReleasePageUrl("a/b"));
    }

    [Theory(DisplayName = "an :update-repo override replaces the repository; blank means the default")]
    [InlineData("owner/test-repo", "owner/test-repo")]
    [InlineData("", "PSOBBAITools/rappyruns-client")]
    [InlineData(null, "PSOBBAITools/rappyruns-client")]
    public void ResolveRepo(string? configured, string expected) => Assert.Equal(expected, UpdateConstants.ResolveRepo(configured));

    // --- startup decision ------------------------------------------------------------

    [Fact(DisplayName = "startup decision applies a newer release")]
    public void DecisionApply() =>
        Assert.Equal(UpdateDecision.Apply, UpdatePolicy.StartupDecision(ReleaseInfo.Parse(ReleaseSample), "0.5.0", true));

    [Fact(DisplayName = "startup decision defers to the manual page when not writable")]
    public void DecisionNotWritable() =>
        Assert.Equal(UpdateDecision.NotWritable, UpdatePolicy.StartupDecision(ReleaseInfo.Parse(ReleaseSample), "0.5.0", false));

    [Fact(DisplayName = "startup decision is up-to-date on the same version")]
    public void DecisionSame() =>
        Assert.Equal(UpdateDecision.UpToDate, UpdatePolicy.StartupDecision(ReleaseInfo.Parse(ReleaseSample), "0.6.0", true));

    [Fact(DisplayName = "startup decision skips the tag the update helper rolled back")]
    public void DecisionRejected()
    {
        var release = ReleaseInfo.Parse(ReleaseSample);
        Assert.Equal(UpdateDecision.Rejected, UpdatePolicy.StartupDecision(release, "0.5.0", true, "v0.6.0"));
        Assert.Equal(UpdateDecision.Apply, UpdatePolicy.StartupDecision(release, "0.5.0", true, "v0.5.9"));
        // Not newer wins over a stale rejection (updater.lisp order).
        Assert.Equal(UpdateDecision.UpToDate, UpdatePolicy.StartupDecision(release, "0.6.0", true, "v0.6.0"));
    }

    [Fact(DisplayName = "rejected-update-tag reads a fresh file and ignores one older than 3 days")]
    public void RejectedTagFile()
    {
        using var temp = new TempDir("rr-rejected");
        var dir = temp.Path;
        Assert.Null(UpdateFiles.RejectedTag(dir));
        var path = Path.Combine(dir, UpdateFiles.RejectedFileName);
        File.WriteAllText(path, "﻿ v1.0.0 \r\nignored\n", new UTF8Encoding(false));
        Assert.Equal("v1.0.0", UpdateFiles.RejectedTag(dir));
        Assert.Null(UpdateFiles.RejectedTag(dir, DateTime.UtcNow.AddDays(3.1)));
        File.WriteAllText(path, "  \n");
        Assert.Null(UpdateFiles.RejectedTag(dir));
    }

    [Fact(DisplayName = "startup decision never updates a dev build")]
    public void DecisionDev() =>
        Assert.Equal(UpdateDecision.UpToDate, UpdatePolicy.StartupDecision(ReleaseInfo.Parse(ReleaseSample), null, true));

    [Fact(DisplayName = "startup decision reports a failed release check")]
    public void DecisionFailed() => Assert.Equal(UpdateDecision.CheckFailed, UpdatePolicy.StartupDecision(null, "0.5.0", true));

    // --- release JSON ----------------------------------------------------------------

    [Fact(DisplayName = "release json yields the tag")]
    public void ReleaseTag() => Assert.Equal("v0.6.0", ReleaseInfo.Parse(ReleaseSample)!.Tag);

    [Fact(DisplayName = "release json picks the client zip asset, not other assets")]
    public void ReleaseAsset() => Assert.Contains("download/v0.6.0/RappyRunsClient.zip", ReleaseInfo.Parse(ReleaseSample)!.AssetUrl);

    [Fact(DisplayName = "release json carries the asset size")]
    public void ReleaseSize() => Assert.Equal(12345678, ReleaseInfo.Parse(ReleaseSample)!.AssetSize);

    [Fact(DisplayName = "release without the zip asset is ignored")]
    public void ReleaseWithoutAsset() => Assert.Null(ReleaseInfo.Parse(
        """{"tag_name": "v0.6.0", "assets": [{"name": "other.zip", "browser_download_url": "https://x/o.zip"}]}"""));

    [Theory(DisplayName = "empty and malformed responses are ignored")]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"assets": []}""")]
    [InlineData(null)]
    public void ReleaseMalformed(string? body) => Assert.Null(ReleaseInfo.Parse(body));

    [Fact(DisplayName = "parse-release-json matches the Lisp golden")]
    public void ReleaseGolden()
    {
        foreach (var c in Golden.Load("api/api-golden.json").GetProperty("releases").EnumerateArray())
        {
            var r = ReleaseInfo.Parse(c.GetProperty("body").GetString());
            var tag = c.GetProperty("tag");
            if (tag.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(r);
                continue;
            }
            Assert.Equal(tag.GetString(), r!.Tag);
            Assert.Equal(c.GetProperty("url").GetString(), r.AssetUrl);
            var size = c.GetProperty("size");
            Assert.Equal(size.ValueKind == JsonValueKind.Null ? null : size.GetInt64(), r.AssetSize);
        }
    }

    // --- discovery and download ------------------------------------------------------

    [Fact(DisplayName = "the release check asks GitHub with the Accept header; non-200 means no update")]
    public async Task FetchLatest()
    {
        var handler = FakeHandler.Always(200, ReleaseSample);
        var updater = new SelfUpdater(new HttpTransport(handler), () => "", "0.5.0", _dir.Path);
        var release = await updater.FetchLatestReleaseAsync();
        Assert.Equal("v0.6.0", release!.Tag);
        var r = handler.Requests[0];
        Assert.Equal("https://api.github.com/repos/PSOBBAITools/rappyruns-client/releases/latest", r.Url);
        Assert.Equal("application/vnd.github+json", r.Accept);
        Assert.Equal("ephinea-ta-client", r.UserAgent);
        Assert.Null(r.Authorization);
        Assert.True(updater.IsNewer(release));

        var limited = new SelfUpdater(new HttpTransport(FakeHandler.Always(403, ReleaseSample)), () => "o/r", "0.5.0", _dir.Path);
        Assert.Null(await limited.FetchLatestReleaseAsync());
        var offline = new SelfUpdater(new HttpTransport(new ThrowingHandler(() => new HttpRequestException("down"))),
            () => null, "0.5.0", _dir.Path);
        Assert.Null(await offline.FetchLatestReleaseAsync());
        var (decision, _) = await offline.StartupCheckAsync();
        Assert.Equal(UpdateDecision.CheckFailed, decision);
    }

    [Fact(DisplayName = "the :update-repo override is read at call time")]
    public async Task RepoOverride()
    {
        var repo = "";
        var handler = FakeHandler.Always(404);
        var updater = new SelfUpdater(new HttpTransport(handler), () => repo, "0.5.0", _dir.Path);
        repo = "owner/test-repo";
        await updater.FetchLatestReleaseAsync();
        Assert.Equal("https://api.github.com/repos/owner/test-repo/releases/latest", handler.Requests[0].Url);
        Assert.Equal("https://github.com/owner/test-repo/releases/latest", updater.ReleasePageUrl);
    }

    [Fact(DisplayName = "startup check: apply only when newer and writable")]
    public async Task StartupCheck()
    {
        var updater = new SelfUpdater(new HttpTransport(FakeHandler.Always(200, ReleaseSample)), () => null, "0.5.0", _dir.Path);
        var (decision, release) = await updater.StartupCheckAsync();
        Assert.Equal(UpdateDecision.Apply, decision);
        Assert.NotNull(release);
        var missingDir = new SelfUpdater(new HttpTransport(FakeHandler.Always(200, ReleaseSample)), () => null, "0.5.0", P("missing"));
        Assert.Equal(UpdateDecision.NotWritable, (await missingDir.StartupCheckAsync()).Decision);
    }

    private static byte[] ZipBytes(params (string Name, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var s = entry.Open();
                s.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return memory.ToArray();
    }

    private sealed class BytesHandler(int status, byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new ByteArrayContent(body) });
    }

    [Fact(DisplayName = "download keeps a verified zip and removes anything else")]
    public async Task DownloadVerifies()
    {
        var zip = ZipBytes(("RappyRunsClient.exe", "new"));
        var target = P("dl.zip");
        var ok = new SelfUpdater(new HttpTransport(new BytesHandler(200, zip)), () => null, "0.5.0", _dir.Path);
        Assert.Equal(target, await ok.DownloadAsync(new ReleaseInfo("v1.0.0", "https://x/a.zip", zip.Length), target));
        Assert.True(File.Exists(target));

        Assert.Null(await ok.DownloadAsync(new ReleaseInfo("v1.0.0", "https://x/a.zip", zip.Length + 1), target));
        Assert.False(File.Exists(target));

        var html = new SelfUpdater(new HttpTransport(new BytesHandler(200, "<html>\n\n"u8.ToArray())), () => null, "0.5.0", _dir.Path);
        Assert.Null(await html.DownloadAsync(new ReleaseInfo("v1.0.0", "https://x/a.zip", null), target));
        Assert.False(File.Exists(target));

        var notFound = new SelfUpdater(new HttpTransport(new BytesHandler(404, zip)), () => null, "0.5.0", _dir.Path);
        Assert.Null(await notFound.DownloadAsync(new ReleaseInfo("v1.0.0", "https://x/a.zip", null), target));
        Assert.False(File.Exists(target));
    }

    [Fact(DisplayName = "download progress is shown once per megabyte with the total rounded up")]
    public void MegabyteProgress()
    {
        var shown = new List<(long, long?)>();
        var progress = SelfUpdater.MegabyteProgress((mb, total) => shown.Add((mb, total)));
        const long mb1 = 1048576;
        foreach (var done in new[] { 65536L, 131072, mb1 - 1, mb1, mb1 + 65536, 2 * mb1 + 5 })
            progress(done, 3 * mb1 + 1);
        Assert.Equal([(0L, (long?)4), (1L, (long?)4), (2L, (long?)4)], shown);
        var chunked = new List<(long, long?)>();
        SelfUpdater.MegabyteProgress((mb, total) => chunked.Add((mb, total)))(10, null);
        Assert.Equal([(0L, (long?)null)], chunked);
    }

    // --- zip verification ------------------------------------------------------------

    [Fact(DisplayName = "zip check passes on matching size and magic / without size / fails on size mismatch / html / missing")]
    public void ZipCheck()
    {
        var path = P("eta-test-update.zip");
        File.WriteAllBytes(path, [80, 75, 3, 4, 9, 9, 9, 9]);
        Assert.True(UpdateFiles.IsValidZip(path, 8));      // zip check passes on matching size and magic
        Assert.True(UpdateFiles.IsValidZip(path, null));   // zip check passes without an expected size
        Assert.False(UpdateFiles.IsValidZip(path, 7));     // zip check fails on a size mismatch
        File.WriteAllBytes(path, [60, 104, 116, 109, 108, 62, 10, 10]);
        Assert.False(UpdateFiles.IsValidZip(path, 8));     // zip check fails on an html error page
        Assert.False(UpdateFiles.IsValidZip(P("eta-no-such-file.zip"), null)); // zip check fails on a missing file
        File.WriteAllBytes(path, [80, 75, 3]);
        Assert.False(UpdateFiles.IsValidZip(path, null));  // shorter than the magic
    }

    [Fact(DisplayName = "the install folder writability probe")]
    public void Writable()
    {
        Assert.True(UpdateFiles.IsDirWritable(_dir.Path));
        Assert.False(File.Exists(P(UpdateConstants.WriteProbeName)));
        Assert.False(UpdateFiles.IsDirWritable(P("no-such-dir")));
    }

    [Fact(DisplayName = "the temp paths are %TEMP%-based and resolved per call")]
    public void TempPaths()
    {
        Assert.Equal(Path.Combine("T", "RappyRunsClient-update.zip"), UpdateFiles.ZipPath("T"));
        Assert.Equal(Path.Combine("T", "rappyruns-update-stage"), UpdateFiles.StageDir("T"));
        var original = Environment.GetEnvironmentVariable("TEMP");
        try
        {
            Environment.SetEnvironmentVariable("TEMP", _dir.Path);
            Assert.Equal(Path.Combine(_dir.Path, "RappyRunsClient-update.zip"), UpdateFiles.ZipPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMP", original);
        }
    }

    // --- the apply step --------------------------------------------------------------

    private sealed record Install(string Dir, string Exe, string Temp, string Zip, string Stage);

    private Install MakeInstall(string exeName = "RappyRunsClient.exe", bool separatorBackslash = false,
        bool withExe = true, bool withFfmpeg = true)
    {
        var dir = P("Rappy Runs's install");
        var temp = P("temp");
        Directory.CreateDirectory(Path.Combine(dir, "data", "pin-share"));
        Directory.CreateDirectory(temp);
        var exe = Path.Combine(dir, exeName);
        File.WriteAllText(exe, "old exe");
        File.WriteAllText(Path.Combine(dir, "data", "quest-triggers.sexp"), "old triggers");
        File.WriteAllText(Path.Combine(dir, "data", "user-kept.txt"), "keep me");
        File.WriteAllText(Path.Combine(dir, "data", "pin-share", "init.lua"), "old lua");
        char sep = separatorBackslash ? '\\' : '/';
        var entries = new List<(string, string)>
        {
            ($"data{sep}quest-triggers.sexp", "new triggers"),
            ($"data{sep}pin-share{sep}init.lua", "new lua"),
            ($"data{sep}pin-share{sep}pinshare-input.dll", "new dll"),
        };
        if (withExe) entries.Insert(0, ("RappyRunsClient.exe", "new exe"));
        if (withFfmpeg) entries.Add(($"ffmpeg{sep}ffmpeg.exe", "new ffmpeg"));
        var zip = Path.Combine(temp, UpdateConstants.ZipFileName);
        File.WriteAllBytes(zip, ZipBytes([.. entries]));
        return new Install(dir, exe, temp, zip, Path.Combine(temp, UpdateConstants.StageDirName));
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(parts));

    [Theory(DisplayName = "apply: stage, move the exe aside, install the new exe and merge data\\ and ffmpeg\\")]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyInstalls(bool backslashes)
    {
        var i = MakeInstall(separatorBackslash: backslashes);
        var staged = UpdateInstaller.Stage(i.Zip, i.Stage);
        Assert.Equal("new exe", File.ReadAllText(staged.NewExePath));
        Assert.Equal("old exe", File.ReadAllText(i.Exe)); // script stages the zip before touching the install
        var installed = UpdateInstaller.Install(staged, i.Exe, i.Dir);
        Assert.Equal("new exe", Read(i.Dir, "RappyRunsClient.exe"));
        Assert.Equal("old exe", Read(i.Dir, "RappyRunsClient.exe.old")); // the running exe is never deleted, only moved
        Assert.Equal("new triggers", Read(i.Dir, "data", "quest-triggers.sexp"));
        Assert.Equal("keep me", Read(i.Dir, "data", "user-kept.txt"));
        Assert.Equal("new lua", Read(i.Dir, "data", "pin-share", "init.lua"));
        Assert.Equal("new dll", Read(i.Dir, "data", "pin-share", "pinshare-input.dll"));
        Assert.Equal("new ffmpeg", Read(i.Dir, "ffmpeg", "ffmpeg.exe"));
        Assert.Equal(Path.Combine(i.Dir, "RappyRunsClient.exe"), installed.TargetExe);
        Assert.True(Directory.Exists(Path.Combine(i.Stage, "data", "pin-share")));
        UpdateInstaller.Cleanup(i.Zip, i.Stage);
        Assert.False(File.Exists(i.Zip));
        Assert.False(Directory.Exists(i.Stage));
    }

    [Fact(DisplayName = "script installs under the canonical exe name (a differing running name becomes .old)")]
    public void ApplyRenamesDifferingExe()
    {
        var i = MakeInstall(exeName: "RappyRunsClient (1).exe");
        UpdateInstaller.Install(UpdateInstaller.Stage(i.Zip, i.Stage), i.Exe, i.Dir);
        Assert.Equal("new exe", Read(i.Dir, "RappyRunsClient.exe"));
        Assert.Equal("old exe", Read(i.Dir, "RappyRunsClient (1).exe.old"));
        Assert.False(File.Exists(i.Exe));
    }

    [Fact(DisplayName = "script verifies the staged exe (no RappyRunsClient.exe in the update zip)")]
    public void StageRequiresExe()
    {
        var i = MakeInstall(withExe: false);
        var ex = Assert.Throws<UpdateException>(() => UpdateInstaller.Stage(i.Zip, i.Stage));
        Assert.Contains("no RappyRunsClient.exe in the update zip", ex.Message);
        Assert.Equal("old exe", File.ReadAllText(i.Exe));
    }

    [Fact(DisplayName = "a corrupt zip fails staging without touching the install")]
    public void StageCorrupt()
    {
        var i = MakeInstall();
        File.WriteAllBytes(i.Zip, [80, 75, 3, 4, 1, 2, 3]);
        Assert.Throws<UpdateException>(() => UpdateInstaller.Stage(i.Zip, i.Stage));
        Assert.Equal("old exe", File.ReadAllText(i.Exe));
    }

    [Fact(DisplayName = "zip entries escaping the stage folder are refused")]
    public void StageZipSlip()
    {
        var i = MakeInstall();
        File.WriteAllBytes(i.Zip, ZipBytes(("RappyRunsClient.exe", "x"), ("..\\..\\evil.txt", "x")));
        Assert.Throws<UpdateException>(() => UpdateInstaller.Stage(i.Zip, i.Stage));
        Assert.False(File.Exists(P("evil.txt")));
    }

    [Fact(DisplayName = "script rolls the .old exe back on failure")]
    public void RollbackOnFailure()
    {
        var i = MakeInstall(exeName: "OldName.exe");
        var staged = UpdateInstaller.Stage(i.Zip, i.Stage);
        // Make data\quest-triggers.sexp unwritable: the merge fails after the exe swap.
        var locked = Path.Combine(i.Dir, "data", "quest-triggers.sexp");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var log = new List<string>();
            Assert.Throws<UpdateException>(() => UpdateInstaller.Install(staged, i.Exe, i.Dir, log.Add));
            Assert.Contains(log, l => l.StartsWith("update failed", StringComparison.Ordinal));
        }
        Assert.Equal("old exe", File.ReadAllText(i.Exe));
        Assert.False(File.Exists(i.Exe + ".old"));
        // a failed differing-name update drops the half-installed new exe
        Assert.False(File.Exists(Path.Combine(i.Dir, "RappyRunsClient.exe")));
    }

    [Fact(DisplayName = "a failed same-name update restores the old exe over the half-installed one")]
    public void RollbackSameName()
    {
        var i = MakeInstall();
        var staged = UpdateInstaller.Stage(i.Zip, i.Stage);
        var locked = Path.Combine(i.Dir, "data", "quest-triggers.sexp");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<UpdateException>(() => UpdateInstaller.Install(staged, i.Exe, i.Dir));
        Assert.Equal("old exe", File.ReadAllText(i.Exe));
        Assert.False(File.Exists(i.Exe + ".old"));
    }

    [Fact(DisplayName = "script treats ffmpeg as best effort")]
    public void FfmpegBestEffort()
    {
        var i = MakeInstall();
        Directory.CreateDirectory(Path.Combine(i.Dir, "ffmpeg"));
        var ffmpeg = Path.Combine(i.Dir, "ffmpeg", "ffmpeg.exe");
        File.WriteAllText(ffmpeg, "running ffmpeg");
        var log = new List<string>();
        using (new FileStream(ffmpeg, FileMode.Open, FileAccess.Read, FileShare.None))
            UpdateInstaller.Install(UpdateInstaller.Stage(i.Zip, i.Stage), i.Exe, i.Dir, log.Add);
        Assert.Equal("new exe", Read(i.Dir, "RappyRunsClient.exe"));
        Assert.Equal("running ffmpeg", File.ReadAllText(ffmpeg));
        Assert.Contains(log, l => l.StartsWith("ffmpeg update skipped", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "a locked running exe that cannot be moved aborts before any change")]
    public void MoveFails()
    {
        var i = MakeInstall();
        var staged = UpdateInstaller.Stage(i.Zip, i.Stage);
        using (new FileStream(i.Exe, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<UpdateException>(() =>
                UpdateInstaller.Install(staged, i.Exe, i.Dir, moveAttempts: 2, retryDelay: TimeSpan.FromMilliseconds(10)));
            Assert.Equal("could not move the old exe aside", ex.Message);
        }
        Assert.Equal("old exe", File.ReadAllText(i.Exe));
        Assert.Equal("old triggers", Read(i.Dir, "data", "quest-triggers.sexp"));
    }

    [Fact(DisplayName = "explicit rollback (launch failure) restores the old exe")]
    public void ExplicitRollback()
    {
        var i = MakeInstall();
        var installed = UpdateInstaller.Install(UpdateInstaller.Stage(i.Zip, i.Stage), i.Exe, i.Dir);
        Assert.True(UpdateInstaller.Rollback(installed));
        Assert.Equal("old exe", File.ReadAllText(i.Exe));
        Assert.False(File.Exists(installed.OldExe));
    }

    [Fact(DisplayName = "a failed update from another exe name restores an existing RappyRunsClient.exe it never owned")]
    public void RollbackKeepsForeignTarget()
    {
        var i = MakeInstall(exeName: "OldName.exe");
        var canonical = Path.Combine(i.Dir, "RappyRunsClient.exe");
        File.WriteAllText(canonical, "other install");
        var staged = UpdateInstaller.Stage(i.Zip, i.Stage);
        var locked = Path.Combine(i.Dir, "data", "quest-triggers.sexp");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<UpdateException>(() => UpdateInstaller.Install(staged, i.Exe, i.Dir));
        Assert.Equal("old exe", File.ReadAllText(i.Exe));
        Assert.Equal("other install", File.ReadAllText(canonical));
        Assert.Empty(Directory.GetFiles(i.Dir, "*.old"));
    }

    [Fact(DisplayName = "an update from another exe name moves an existing RappyRunsClient.exe aside; rollback puts both back")]
    public void ForeignTargetMovedAsideAndRestored()
    {
        var i = MakeInstall(exeName: "OldName.exe");
        var canonical = Path.Combine(i.Dir, "RappyRunsClient.exe");
        File.WriteAllText(canonical, "other install");
        var installed = UpdateInstaller.Install(UpdateInstaller.Stage(i.Zip, i.Stage), i.Exe, i.Dir);
        Assert.Equal("new exe", File.ReadAllText(canonical));
        Assert.Equal("other install", File.ReadAllText(canonical + ".old"));
        Assert.Equal("old exe", File.ReadAllText(i.Exe + ".old"));
        Assert.True(UpdateInstaller.Rollback(installed));
        Assert.Equal("old exe", File.ReadAllText(i.Exe));
        Assert.Equal("other install", File.ReadAllText(canonical));
        Assert.Empty(Directory.GetFiles(i.Dir, "*.old"));
    }

    [Fact(DisplayName = "startup cleanup finds every *.exe.old, whatever the previous exe was called")]
    public void OldExeNamesIn()
    {
        var install = P("install-old-names");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "RenamedClient.exe.old"), "x");
        File.WriteAllText(Path.Combine(install, "RappyRunsClient.exe.old"), "x");
        File.WriteAllText(Path.Combine(install, "notes.txt.old"), "x");
        var names = UpdateFiles.OldExeNamesIn(install).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["RappyRunsClient.exe", "RenamedClient.exe"], names);
        Assert.True(UpdateFiles.CleanupOldUpdateFiles(install, P("tmp-old-names"), names));
        Assert.Equal([Path.Combine(install, "notes.txt.old")], Directory.GetFiles(install));
        Assert.Empty(UpdateFiles.OldExeNamesIn(P("missing-folder")));
    }

    [Fact(DisplayName = "windows-temp-dir: TEMP, then TMP, then home - the order the Lisp bridge updater uses")]
    public void TempDirOrder()
    {
        Assert.Equal(@"C:\T", UpdateFiles.TempDir(n => n switch { "TEMP" => @"C:\T", "TMP" => @"C:\M", _ => null }));
        Assert.Equal(@"C:\M", UpdateFiles.TempDir(n => n switch { "TEMP" => "", "TMP" => @"C:\M", _ => null }));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), UpdateFiles.TempDir(_ => null));
    }

    [Fact(DisplayName = "a Lisp-built zip (backslash entries, data + ffmpeg) installs for a downgrade")]
    public void LispZipDowngrade()
    {
        var i = MakeInstall(separatorBackslash: true);
        UpdateInstaller.Install(UpdateInstaller.Stage(i.Zip, i.Stage), i.Exe, i.Dir);
        Assert.True(File.Exists(Path.Combine(i.Dir, "data", "pin-share", "pinshare-input.dll")));
    }

    // --- startup cleanup -------------------------------------------------------------

    [Fact(DisplayName = "startup cleanup removes the .old exe and the temp leftovers")]
    public void Cleanup()
    {
        var install = P("install");
        var temp = P("tmp");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(Path.Combine(temp, UpdateConstants.StageDirName, "data"));
        File.WriteAllText(Path.Combine(install, "RappyRunsClient.exe.old"), "x");
        File.WriteAllText(Path.Combine(install, "Other.exe.old"), "x");
        File.WriteAllText(Path.Combine(temp, "rappyruns-update.ps1"), "x");
        File.WriteAllText(Path.Combine(temp, "RappyRunsClient-update.zip"), "x");
        File.WriteAllText(Path.Combine(temp, "rappyruns-update.log"), "x");
        Assert.True(UpdateFiles.CleanupOldUpdateFiles(install, temp, ["Other.exe"]));
        Assert.Empty(Directory.GetFiles(install));
        Assert.Equal([Path.Combine(temp, "rappyruns-update.log")], Directory.GetFileSystemEntries(temp));
        Assert.True(UpdateFiles.CleanupOldUpdateFiles(install, temp)); // nothing left is fine
    }

    [Fact(DisplayName = "startup cleanup retries while the previous process still holds the .old exe")]
    public async Task CleanupRetries()
    {
        var install = P("install2");
        Directory.CreateDirectory(install);
        var old = Path.Combine(install, "RappyRunsClient.exe.old");
        File.WriteAllText(old, "x");
        var holder = new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None);
        var cleanup = UpdateFiles.CleanupOldUpdateFilesAsync(install, P("tmp2"), attempts: 50, interval: TimeSpan.FromMilliseconds(20));
        await Task.Delay(100);
        await holder.DisposeAsync();
        Assert.True(await cleanup);
        Assert.False(File.Exists(old));
        using (new FileStream(Path.Combine(install, "x"), FileMode.Create)) { }
        File.WriteAllText(old, "x");
        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(await UpdateFiles.CleanupOldUpdateFilesAsync(install, P("tmp2"), attempts: 2, interval: TimeSpan.FromMilliseconds(5)));
    }

    // --- busy deferral ---------------------------------------------------------------

    [Fact(DisplayName = "an update downloaded while idle applies at once")]
    public void DeferIdle()
    {
        var d = new DeferredUpdate();
        d.NoteActivity(false, false);
        Assert.True(d.OfferOrDefer("z", "v1"));
        Assert.Null(d.Pending);
    }

    [Fact(DisplayName = "an update downloaded mid-run waits and applies exactly once at the first idle frame")]
    public void DeferBusy()
    {
        var d = new DeferredUpdate();
        Assert.Null(d.NoteActivity(true, false));
        Assert.False(d.OfferOrDefer("z", "v1"));
        Assert.Equal(new DeferredUpdate.ReadyUpdate("z", "v1"), d.Pending);
        Assert.Null(d.NoteActivity(false, true)); // recording still counts as busy
        Assert.True(d.IsBusy);
        Assert.Equal(new DeferredUpdate.ReadyUpdate("z", "v1"), d.NoteActivity(false, false));
        Assert.Null(d.NoteActivity(false, false));
        Assert.Null(d.Pending);
    }

    [Fact(DisplayName = "a newer download replaces the parked one; applied at once, nothing stays parked (PR #330 review)")]
    public void DeferReplacesParked()
    {
        var d = new DeferredUpdate();
        d.NoteActivity(true, false);
        Assert.False(d.OfferOrDefer("z1", "v1"));
        Assert.False(d.OfferOrDefer("z2", "v2"));
        Assert.Equal(new DeferredUpdate.ReadyUpdate("z2", "v2"), d.NoteActivity(false, false));
        Assert.True(d.OfferOrDefer("z3", "v3"));
        Assert.Null(d.Pending);
        Assert.Null(d.NoteActivity(false, false));
    }
}

using Microsoft.Win32;
using RappyRuns.Win.Shell;

namespace RappyRuns.Tests.Shell;

// No Lisp tests cover autostart-win32.lisp (it is LispWorks-only FFI); these
// pin the behaviour of autostart-enabled-p / set-autostart! (core §2.5) plus
// the C# additions (normalized comparison, startup repair §10.7 #11).
public class AutostartCommandTests
{
    [Fact]
    public void FormatQuotesThePathAndAddsMinimized() =>
        Assert.Equal("\"C:\\Games\\Rappy Runs\\RappyRunsClient.exe\" --minimized",
            AutostartCommand.Format(@"C:\Games\Rappy Runs\RappyRunsClient.exe"));

    [Theory]
    [InlineData("\"C:\\A B\\x.exe\" --minimized", @"C:\A B\x.exe")]
    [InlineData("\"C:\\A B\\x.exe\"", @"C:\A B\x.exe")]
    [InlineData("  \"C:\\x.exe\"   --MINIMIZED ", @"C:\x.exe")]
    [InlineData(@"C:\x.exe --minimized", @"C:\x.exe")]
    [InlineData(@"C:\x.exe", @"C:\x.exe")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("\"\" --minimized", null)]
    [InlineData("\"unterminated", null)]
    public void ExtractPath(string command, string? expected) =>
        Assert.Equal(expected, AutostartCommand.ExtractPath(command));

    [Theory]
    [InlineData(@"C:\Games\RappyRunsClient.exe", @"c:\games\RAPPYRUNSCLIENT.EXE", true)]
    [InlineData(@"C:\Games\RappyRunsClient.exe", @"C:/Games/RappyRunsClient.exe", true)]
    [InlineData(@"C:\Games\sub\..\RappyRunsClient.exe", @"C:\Games\RappyRunsClient.exe", true)]
    [InlineData(@"C:\Games\RappyRunsClient (1).exe", @"C:\Games\RappyRunsClient.exe", false)]
    [InlineData(@"RappyRunsClient.exe", @"RappyRunsClient.exe", false)]
    [InlineData(null, @"C:\x.exe", false)]
    public void SamePath(string? a, string? b, bool expected) =>
        Assert.Equal(expected, AutostartCommand.SamePath(a, b));
}

public sealed class AutostartRegistryTests : IDisposable
{
    private readonly string _keyPath = @"Software\RappyRunsTests\" + Guid.NewGuid().ToString("N") + @"\Run";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rappyruns-autostart-" + Guid.NewGuid().ToString("N"));

    public AutostartRegistryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(Path.GetDirectoryName(_keyPath)!, throwOnMissingSubKey: false);
        try { Registry.CurrentUser.DeleteSubKey(Path.GetDirectoryName(Path.GetDirectoryName(_keyPath))!, throwOnMissingSubKey: false); }
        catch (InvalidOperationException) { } // another run's key is still there
        Directory.Delete(_dir, recursive: true);
    }

    private string Exe(string name = "RappyRunsClient.exe") => Path.Combine(_dir, name);

    private Autostart Make(string? exe) => new(exe, Registry.CurrentUser, _keyPath, Autostart.ValueName);

    private void Write(string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        key.SetValue(Autostart.ValueName, value, RegistryValueKind.String);
    }

    [Fact]
    public void EnableWritesTheCanonicalCommandAndReadsBackEnabled()
    {
        var a = Make(Exe());
        Assert.False(a.IsEnabled());
        Assert.True(a.SetEnabled(true));
        Assert.Equal($"\"{Exe()}\" --minimized", a.ReadRegistered());
        Assert.True(a.IsEnabled());
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath)!;
        Assert.Equal(RegistryValueKind.String, key.GetValueKind(Autostart.ValueName));
    }

    [Fact]
    public void DisableDeletesAndSucceedsWhenAbsent()
    {
        var a = Make(Exe());
        Assert.True(a.SetEnabled(false)); // key does not even exist yet
        a.SetEnabled(true);
        Assert.True(a.SetEnabled(false));
        Assert.Null(a.ReadRegistered());
        Assert.False(a.IsEnabled());
        Assert.True(a.SetEnabled(false));
    }

    [Fact]
    public void DifferentSpellingOfThisExeCountsAsEnabled()
    {
        // What the Lisp client may have registered from argv[0].
        Write($"\"{Exe().ToUpperInvariant()}\" --minimized");
        Assert.True(Make(Exe()).IsEnabled());
    }

    [Fact]
    public void AnotherExeReadsDisabled()
    {
        Write(AutostartCommand.Format(Path.Combine(_dir, "elsewhere", "RappyRunsClient.exe")));
        Assert.False(Make(Exe()).IsEnabled());
    }

    [Fact]
    public void NoExePathCannotEnable()
    {
        var a = Make(""); // ProcessPath unavailable
        Assert.Null(a.CanonicalCommand);
        Assert.False(a.SetEnabled(true));
        Assert.False(a.IsEnabled());
    }

    [Fact]
    public void ReconcileLeavesAMissingValueAlone()
    {
        Assert.Equal(AutostartRepair.NotRegistered, Make(Exe()).ReconcileAtStartup());
        Assert.Null(Make(Exe()).ReadRegistered());
    }

    [Fact]
    public void ReconcileKeepsTheCanonicalValue()
    {
        var a = Make(Exe());
        a.SetEnabled(true);
        Assert.Equal(AutostartRepair.UpToDate, a.ReconcileAtStartup());
    }

    [Fact]
    public void ReconcileRewritesAnotherSpellingOfThisExe()
    {
        Write($"{Exe().ToLowerInvariant()}"); // unquoted, no --minimized
        var a = Make(Exe());
        Assert.Equal(AutostartRepair.Rewritten, a.ReconcileAtStartup());
        Assert.Equal(a.CanonicalCommand, a.ReadRegistered());
    }

    [Fact]
    public void ReconcileRewritesTheOldExeNameGoneFromThisFolder()
    {
        // §10.7 #11: "RappyRunsClient (1).exe" became .old when the update
        // put the new exe under the canonical name.
        Write(AutostartCommand.Format(Exe("RappyRunsClient (1).exe")));
        var a = Make(Exe());
        Assert.False(a.IsEnabled());
        Assert.Equal(AutostartRepair.Rewritten, a.ReconcileAtStartup());
        Assert.True(a.IsEnabled());
    }

    [Fact]
    public void ReconcileLeavesAnExistingForeignExe()
    {
        var other = Exe("RappyRunsClient (1).exe");
        File.WriteAllText(other, "");
        var command = AutostartCommand.Format(other);
        Write(command);
        Assert.Equal(AutostartRepair.LeftForeign, Make(Exe()).ReconcileAtStartup());
        Assert.Equal(command, Make(Exe()).ReadRegistered());
    }

    [Fact]
    public void ReconcileLeavesAMissingExeInAnotherFolder()
    {
        var command = AutostartCommand.Format(Path.Combine(_dir, "moved", "RappyRunsClient.exe"));
        Write(command);
        Assert.Equal(AutostartRepair.LeftForeign, Make(Exe()).ReconcileAtStartup());
        Assert.Equal(command, Make(Exe()).ReadRegistered());
    }

    [Fact]
    public void ContractNames()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", Autostart.RunKeyPath);
        Assert.Equal("RappyRunsClient", Autostart.ValueName);
    }
}

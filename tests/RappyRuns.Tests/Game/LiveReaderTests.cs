using System.Diagnostics;
using RappyRuns.Core.Game;
using RappyRuns.Win.Game;

namespace RappyRuns.Tests.Game;

/// <summary>Smoke tests of the Win32 side against this test process and signed/unsigned files on disk.</summary>
public class LiveReaderTests
{
    [Fact(DisplayName = "authenticode: an embedded signature is Valid with its signer CN")]
    public void SignedFile()
    {
        var (status, signer) = Authenticode.Verify(typeof(object).Assembly.Location);
        Assert.Equal(SignatureStatus.Valid, status);
        Assert.False(string.IsNullOrEmpty(signer)); // ".NET" for CoreLib - the leaf certificate CN
        Assert.False(PsobbTables.SignatureTrusted(status, signer));
    }

    [Fact(DisplayName = "authenticode: an installed Ephinea PsoBB.exe (when present) is the trusted signer")]
    public void EphineaClientIfInstalled()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "EphineaPSO", "PsoBB.exe");
        if (!File.Exists(exe)) return; // developer machines only
        var (status, signer) = Authenticode.Verify(exe);
        Assert.Equal((SignatureStatus.Valid, "Terry Chatman"), (status, signer));
        Assert.True(PsobbTables.SignatureTrusted(status, signer));
    }

    [Fact(DisplayName = "authenticode: an unsigned PE is Unsigned")]
    public void UnsignedFile() =>
        Assert.Equal((SignatureStatus.Unsigned, (string?)null), Authenticode.Verify(typeof(Detector).Assembly.Location));

    [Fact(DisplayName = "authenticode: a missing file is Invalid")]
    public void MissingFile() =>
        Assert.Equal(SignatureStatus.Invalid, Authenticode.Verify(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")).Status);

    [Fact(DisplayName = "live reader: opens a process with minimal rights, knows it is alive and where its exe is")]
    public void OwnProcess()
    {
        var connector = new PsobbConnector();
        using var reader = connector.OpenReaderFor(0, Environment.ProcessId);
        Assert.NotNull(reader);
        Assert.True(reader.IsAlive);
        Assert.Equal(Path.GetFullPath(Process.GetCurrentProcess().MainModule!.FileName), Path.GetFullPath(reader.ImagePath!),
            StringComparer.OrdinalIgnoreCase);
        // A 32-bit target's address space ends at 4 GB: anything past it is never read (or wrapped).
        Assert.Null(reader.ReadBlock(0x1_0000_0000L, 4));
        Assert.Null(reader.ReadBlock(-4, 4));
        // Page zero is never mapped: a failed read keeps its error code.
        Assert.Null(reader.ReadBlock(0, 4));
        Assert.NotNull(reader.LastReadError);
    }

    [Fact(DisplayName = "live reader: OpenProcess failure yields null (no such pid)")]
    public void NoSuchProcess() => Assert.Null(new PsobbConnector().OpenReaderFor(0, 0x7FFFFFF0));
}

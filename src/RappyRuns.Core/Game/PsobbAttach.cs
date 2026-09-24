namespace RappyRuns.Core.Game;

/// <summary>
/// The pure half of attaching to PSOBB (win32.lisp:589 choose-psobb-reader,
/// main.lisp:52 psobb-trust-rejection): which candidate window to read and
/// whether its exe may be read at all. The Win32 legwork lives in
/// RappyRuns.Win.Game; the decisions are here so they are testable.
/// </summary>
public static class PsobbAttach
{
    /// <summary>win32.lisp:198 +psobb-window-names+: exact top-level window titles.</summary>
    public static IReadOnlyList<string> WindowNames { get; } =
        ["Ephinea: Phantasy Star Online Blue Burst", "PHANTASY STAR ONLINE Blue Burst"];

    /// <summary>
    /// win32.lisp:589 choose-psobb-reader. One candidate is returned untouched
    /// (the poll loop trust-checks it). With several (2-window play) only a
    /// file-based signature check runs on each: none trusted → the first
    /// untrusted one, unread (so it is reported "not official"); one trusted →
    /// it; several → the first trusted one running a quest, else the first
    /// trusted. Every candidate not chosen is closed.
    /// </summary>
    public static T? Choose<T>(IReadOnlyList<T> readers, Func<T, bool> signatureTrusted, Func<T, bool> questLoaded, Action<T> close)
        where T : class
    {
        if (readers.Count == 0) return null;
        if (readers.Count == 1) return readers[0];
        var trusted = new List<T>();
        var untrusted = new List<T>();
        foreach (var r in readers) (signatureTrusted(r) ? trusted : untrusted).Add(r);
        T chosen;
        if (trusted.Count == 0) chosen = untrusted[0];
        else if (trusted.Count == 1) chosen = trusted[0];
        else chosen = trusted.FirstOrDefault(questLoaded) ?? trusted[0];
        foreach (var r in readers)
        {
            if (!ReferenceEquals(r, chosen)) close(r);
        }
        return chosen;
    }

    /// <summary>
    /// main.lisp:52 psobb-trust-rejection: null when the exe is the signed
    /// official client, else the rejection. A missing image path fails closed
    /// (Invalid). <paramref name="cached"/> for the same pid is reused so the
    /// once-a-second search does not re-hash the exe.
    /// </summary>
    public static PsobbRejection? TrustRejection(int pid, PsobbRejection? cached, Func<string?> imagePath,
        Func<string, (SignatureStatus Status, string? Signer)> verify)
    {
        if (cached is not null && cached.Pid == pid) return cached;
        var path = imagePath();
        var (status, signer) = path is not null ? verify(path) : (SignatureStatus.Invalid, null);
        return PsobbTables.SignatureTrusted(status, signer) ? null : new PsobbRejection(pid, path, status, signer);
    }
}

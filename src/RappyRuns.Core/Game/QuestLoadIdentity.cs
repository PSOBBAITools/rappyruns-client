namespace RappyRuns.Core.Game;

/// <summary>What one <see cref="QuestLoadIdentity.Observe"/> saw.</summary>
public enum QuestLoadChange
{
    /// <summary>The same load as before, or a quest whose name is not read yet: nothing to do.</summary>
    Unchanged,

    /// <summary>No quest is loaded: the load is forgotten (<see cref="QuestLoadIdentity.Forget"/>).</summary>
    Unloaded,

    /// <summary>A different quest (pointer or name) was loaded: a new load.</summary>
    NewQuest,

    /// <summary>The same quest, adopted anew after <see cref="QuestLoadIdentity.Refetch"/>: a new load.</summary>
    Refetched,
}

/// <summary>
/// Which quest load is current, and its number (S36, shared by S46): the
/// ghost fetch and the pin-set fetch both key off the quest POINTER plus its
/// name (a different name at the same pointer is a new load too, when no
/// lobby frame was seen in between), and both need a fetch started for one
/// load to land only while that load is still current - the pointer alone
/// cannot tell a later quest loaded at the same address (or a relaunched
/// game) from the one the fetch was for, so every new load, forget and
/// refetch bumps <see cref="Load"/>.
/// </summary>
/// <remarks>
/// Not thread-safe: the owner calls it under its own lock, the same one that
/// guards whatever the load owns (the ghost, the pin set), so "is this load
/// still current?" and the write it guards are one step.
/// </remarks>
public sealed class QuestLoadIdentity
{
    private long _ptr;     // 0 = none
    private string? _name;
    private bool _refetch; // Refetch asked: the next sighting adopts the same quest anew

    /// <summary>The current load's number; starts at 1 and only grows.</summary>
    public long Load { get; private set; } = 1;

    /// <summary>The quest pointer of the current load; null while none is loaded.</summary>
    public long? Ptr => _ptr > 0 ? _ptr : null;

    /// <summary>True while <paramref name="load"/> (a <see cref="Load"/> taken earlier) is still the current load.</summary>
    public bool IsCurrent(long load) => load == Load;

    /// <summary>
    /// Feeds one reading of the loaded quest. A pointer of 0 or less (no
    /// quest) forgets the load; an unread name changes nothing; a pointer and
    /// name other than the current load's (or any sighting after
    /// <see cref="Refetch"/>) adopt it as a new load.
    /// </summary>
    public QuestLoadChange Observe(long questPtr, string? questName)
    {
        if (questPtr <= 0)
        {
            Forget();
            return QuestLoadChange.Unloaded;
        }
        if (questName is null) return QuestLoadChange.Unchanged;
        var same = questPtr == _ptr && questName == _name;
        if (same && !_refetch) return QuestLoadChange.Unchanged;
        _refetch = false;
        _ptr = questPtr;
        _name = questName;
        Load++;
        return same ? QuestLoadChange.Refetched : QuestLoadChange.NewQuest;
    }

    /// <summary>No load: a fetch still in flight lands nowhere, and the next sighting is a new load.</summary>
    public void Forget()
    {
        _ptr = 0;
        _name = null;
        _refetch = false;
        Load++;
    }

    /// <summary>
    /// Adopt the current quest anew on the next sighting; a fetch already in
    /// flight for it is stale from now on.
    /// </summary>
    public void Refetch()
    {
        _refetch = true;
        Load++;
    }
}

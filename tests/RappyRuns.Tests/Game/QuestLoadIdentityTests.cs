using RappyRuns.Core.Game;

namespace RappyRuns.Tests.Game;

public class QuestLoadIdentityTests
{
    [Fact(DisplayName = "quest load: a new pointer or name is a new load; an unread name or the same load changes nothing (S46)")]
    public void Loads()
    {
        var id = new QuestLoadIdentity();
        Assert.Equal(1, id.Load);
        Assert.Null(id.Ptr);
        Assert.Equal(QuestLoadChange.Unchanged, id.Observe(0x1000, null));
        Assert.Equal(1, id.Load);
        Assert.Equal(QuestLoadChange.NewQuest, id.Observe(0x1000, "Lost HEAT SWORD"));
        var first = id.Load;
        Assert.Equal(0x1000, id.Ptr);
        Assert.Equal(QuestLoadChange.Unchanged, id.Observe(0x1000, "Lost HEAT SWORD"));
        Assert.True(id.IsCurrent(first));
        // Another quest at the same address, with no lobby frame in between.
        Assert.Equal(QuestLoadChange.NewQuest, id.Observe(0x1000, "Mop-up Operation #1"));
        Assert.False(id.IsCurrent(first));
    }

    [Fact(DisplayName = "quest load: no quest forgets the load, so the same quest again is a new load; refetch re-adopts it (S46)")]
    public void ForgetAndRefetch()
    {
        var id = new QuestLoadIdentity();
        id.Observe(0x1000, "Lost HEAT SWORD");
        var first = id.Load;
        Assert.Equal(QuestLoadChange.Unloaded, id.Observe(0, null));
        Assert.Null(id.Ptr);
        Assert.False(id.IsCurrent(first));
        Assert.Equal(QuestLoadChange.NewQuest, id.Observe(0x1000, "Lost HEAT SWORD"));

        var loaded = id.Load;
        id.Refetch();
        Assert.False(id.IsCurrent(loaded)); // a reply in flight is stale now
        Assert.Equal(QuestLoadChange.Refetched, id.Observe(0x1000, "Lost HEAT SWORD"));
        Assert.Equal(QuestLoadChange.Unchanged, id.Observe(0x1000, "Lost HEAT SWORD"));
        // A refetch waits through an unread name, and a negative pointer is no quest.
        id.Refetch();
        Assert.Equal(QuestLoadChange.Unchanged, id.Observe(0x1000, null));
        Assert.Equal(QuestLoadChange.Refetched, id.Observe(0x1000, "Lost HEAT SWORD"));
        Assert.Equal(QuestLoadChange.Unloaded, id.Observe(-1, "Lost HEAT SWORD"));
        // A refetch with nothing loaded: the next sighting is still a new quest.
        id.Refetch();
        Assert.Equal(QuestLoadChange.NewQuest, id.Observe(0x1000, "Lost HEAT SWORD"));
        // Forget clears a pending refetch too.
        id.Refetch();
        id.Forget();
        Assert.Equal(QuestLoadChange.NewQuest, id.Observe(0x1000, "Lost HEAT SWORD"));
    }
}

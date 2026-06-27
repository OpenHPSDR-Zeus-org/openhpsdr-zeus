using LiteDB;
using Zeus.Data;

namespace Zeus.Plugins.Host.Tests;

// Behavioural contract for the single-instance database registry that replaced
// ~40 independent `new LiteDatabase(...;Connection=shared)` opens (the LiteDB
// corruption antipattern). Assertions use reference identity and on-disk
// persistence rather than the process-global OpenCount, so they stay correct
// when xUnit runs test classes in parallel.
public sealed class SharedLiteDatabaseTests : IDisposable
{
    private readonly List<string> _paths = new();

    private string TempPath()
    {
        var p = Path.Combine(Path.GetTempPath(), "zeus-sharedlitedb-" + Guid.NewGuid().ToString("N") + ".db");
        _paths.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _paths)
        {
            try { File.Delete(p); } catch { /* best effort */ }
            try { File.Delete(p + "-log"); } catch { /* best effort */ }
        }
    }

    private sealed class Doc
    {
        public int Id { get; set; }
        public string V { get; set; } = "";
    }

    [Fact]
    public void SamePath_SharesOneEngine()
    {
        var p = TempPath();
        using var a = SharedLiteDatabase.Acquire(p);
        using var b = SharedLiteDatabase.Acquire(p);

        Assert.Same(a.Database, b.Database);

        a.Database.GetCollection<Doc>("c").Insert(new Doc { Id = 1, V = "x" });
        // A write through one lease is immediately visible through the other —
        // proof they are the same engine, not two caches over one file.
        Assert.Equal("x", b.Database.GetCollection<Doc>("c").FindById(1).V);
    }

    [Fact]
    public void DistinctPaths_GetDistinctEngines()
    {
        using var a = SharedLiteDatabase.Acquire(TempPath());
        using var b = SharedLiteDatabase.Acquire(TempPath());
        Assert.NotSame(a.Database, b.Database);
    }

    [Fact]
    public void Refcount_KeepsEngineAliveUntilLastRelease_ThenReopensFresh()
    {
        var p = TempPath();
        var a = SharedLiteDatabase.Acquire(p);
        var b = SharedLiteDatabase.Acquire(p);
        var engine = a.Database;

        a.Dispose(); // b still holds a reference
        Assert.Same(engine, b.Database);
        // The engine is still open and usable while any lease is held.
        b.Database.GetCollection<Doc>("c").Insert(new Doc { Id = 1, V = "ok" });

        b.Dispose(); // last release closes + checkpoints to disk

        using var c = SharedLiteDatabase.Acquire(p);
        Assert.NotSame(engine, c.Database); // a brand-new engine after full release
    }

    [Fact]
    public void LastRelease_CheckpointsToDisk_SoReopenSeesData()
    {
        var p = TempPath();
        var a = SharedLiteDatabase.Acquire(p);
        a.Database.GetCollection<Doc>("c").Insert(new Doc { Id = 1, V = "persist" });
        a.Dispose();

        using var b = SharedLiteDatabase.Acquire(p);
        Assert.Equal("persist", b.Database.GetCollection<Doc>("c").FindById(1).V);
    }

    [Fact]
    public void DoubleDispose_IsSafe_AndDoesNotOverRelease()
    {
        var p = TempPath();
        var outer = SharedLiteDatabase.Acquire(p); // keep the engine alive
        var inner = SharedLiteDatabase.Acquire(p);

        inner.Dispose();
        inner.Dispose(); // must be a no-op, not a second decrement

        // The engine is still alive (outer holds it); a double-dispose did not
        // corrupt the refcount.
        outer.Database.GetCollection<Doc>("c").Insert(new Doc { Id = 1, V = "y" });
        Assert.Equal("y", outer.Database.GetCollection<Doc>("c").FindById(1).V);
        outer.Dispose();
    }

    [Fact]
    public void ConcurrentAcquireRelease_DoesNotCorrupt()
    {
        var p = TempPath();
        // Pin the engine open for the whole storm so it is opened/closed exactly
        // once at the boundaries, and many leases come and go in between.
        using (var pin = SharedLiteDatabase.Acquire(p))
        {
            pin.Database.GetCollection<Doc>("c").Insert(new Doc { Id = 1, V = "0" });
            Parallel.For(0, 256, i =>
            {
                using var lease = SharedLiteDatabase.Acquire(p);
                lease.Database.GetCollection<Doc>("c").Upsert(new Doc { Id = 1, V = i.ToString() });
            });
        }

        using var reopen = SharedLiteDatabase.Acquire(p);
        Assert.NotNull(reopen.Database.GetCollection<Doc>("c").FindById(1));
    }
}

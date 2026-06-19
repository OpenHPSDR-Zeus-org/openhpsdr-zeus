using Zeus.Plugins.Host;

namespace Zeus.Plugins.Host.Tests;

public class PluginSettingsStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PluginSettingsStore _store;

    public PluginSettingsStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            "zeus-plugins-store-" + Guid.NewGuid().ToString("N") + ".db");
        _store = new PluginSettingsStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { /* ignore */ }
    }

    [Fact]
    public async Task SetThenGet_RoundTrips_PrimitiveString()
    {
        var s = _store.ForPlugin("com.example.a");
        await s.SetAsync("name", "alice");
        Assert.Equal("alice", await s.GetAsync<string>("name"));
    }

    [Fact]
    public async Task SetThenGet_RoundTrips_Record()
    {
        var s = _store.ForPlugin("com.example.a");
        var sample = new Sample(7, "x");
        await s.SetAsync("k", sample);
        Assert.Equal(sample, await s.GetAsync<Sample>("k"));
    }

    [Fact]
    public async Task Get_MissingKey_ReturnsDefault()
    {
        var s = _store.ForPlugin("com.example.a");
        Assert.Null(await s.GetAsync<string>("absent"));
        Assert.Equal(0, await s.GetAsync<int>("absent"));
    }

    [Fact]
    public async Task ScopedPerPluginId_NoCrossTalk()
    {
        var a = _store.ForPlugin("com.example.a");
        var b = _store.ForPlugin("com.example.b");

        await a.SetAsync("shared-key", "from-a");
        await b.SetAsync("shared-key", "from-b");

        Assert.Equal("from-a", await a.GetAsync<string>("shared-key"));
        Assert.Equal("from-b", await b.GetAsync<string>("shared-key"));
    }

    [Fact]
    public async Task Delete_RemovesKey()
    {
        var s = _store.ForPlugin("com.example.a");
        await s.SetAsync("k", 42);
        Assert.Equal(42, await s.GetAsync<int>("k"));
        await s.DeleteAsync("k");
        Assert.Equal(0, await s.GetAsync<int>("k"));
    }

    [Fact]
    public async Task SurvivesReopen()
    {
        var s = _store.ForPlugin("com.example.a");
        await s.SetAsync("persist", "value-1");

        _store.Dispose();
        using var reopened = new PluginSettingsStore(_dbPath);
        var s2 = reopened.ForPlugin("com.example.a");
        Assert.Equal("value-1", await s2.GetAsync<string>("persist"));
    }

    [Fact]
    public void EmptyPluginId_Throws()
    {
        Assert.Throws<ArgumentException>(() => _store.ForPlugin(""));
    }

    // ---- DumpCollection / RestoreCollection seam (TX Audio Profiles) ----

    [Fact]
    public async Task DumpCollection_CapturesEveryKey()
    {
        var s = _store.ForPlugin("com.example.native");
        await s.SetAsync("drive", 0.7);
        await s.SetAsync("bypass", true);
        await s.SetAsync("label", "punch");

        var dump = _store.DumpCollection("com.example.native");

        Assert.Equal(3, dump.Count);
        Assert.True(dump.ContainsKey("drive"));
        Assert.True(dump.ContainsKey("bypass"));
        Assert.True(dump.ContainsKey("label"));
        // JsonValue strings are opaque to the host but well-formed JSON.
        Assert.Equal("true", dump["bypass"]);
    }

    [Fact]
    public void DumpCollection_EmptyForUnknownPlugin()
    {
        Assert.Empty(_store.DumpCollection("com.example.never-written"));
        Assert.Empty(_store.DumpCollection(""));
    }

    [Fact]
    public async Task RestoreCollection_RoundTrips_ThroughScopedReads()
    {
        var s = _store.ForPlugin("com.example.native");
        await s.SetAsync("drive", 0.7);
        await s.SetAsync("bypass", false);
        var snapshot = _store.DumpCollection("com.example.native");

        // Operator drifts the live values...
        await s.SetAsync("drive", 0.1);
        await s.SetAsync("bypass", true);
        Assert.Equal(0.1, await s.GetAsync<double>("drive"));

        // ...then a profile apply restores the snapshot.
        _store.RestoreCollection("com.example.native", snapshot);

        Assert.Equal(0.7, await s.GetAsync<double>("drive"));
        Assert.False(await s.GetAsync<bool>("bypass"));
    }

    [Fact]
    public async Task RestoreCollection_NoDuplicateRows()
    {
        var s = _store.ForPlugin("com.example.native");
        await s.SetAsync("k", 1);
        var snap = _store.DumpCollection("com.example.native");

        // Restore the same snapshot several times — must not grow the collection
        // (DeleteMany+Insert is idempotent, the PR #387 fix).
        _store.RestoreCollection("com.example.native", snap);
        _store.RestoreCollection("com.example.native", snap);
        _store.RestoreCollection("com.example.native", snap);

        Assert.Single(_store.DumpCollection("com.example.native"));
        Assert.Equal(1, await s.GetAsync<int>("k"));
    }

    [Fact]
    public async Task RestoreCollection_LeavesUnrelatedKeysUntouched()
    {
        var s = _store.ForPlugin("com.example.native");
        await s.SetAsync("a", 1);
        await s.SetAsync("b", 2);

        // Partial snapshot of only "a"; restoring it must not wipe "b".
        _store.RestoreCollection("com.example.native",
            new Dictionary<string, string> { ["a"] = "9" });

        Assert.Equal(9, await s.GetAsync<int>("a"));
        Assert.Equal(2, await s.GetAsync<int>("b"));
    }

    private sealed record Sample(int N, string S);
}

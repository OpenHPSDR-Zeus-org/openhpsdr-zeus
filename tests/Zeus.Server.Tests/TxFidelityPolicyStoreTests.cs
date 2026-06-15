// SPDX-License-Identifier: GPL-2.0-or-later
//
// TX fidelity policy persistence: active station target survives restart while
// the station-profile catalog remains the owner of detailed TX settings.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class TxFidelityPolicyStoreTests : IDisposable
{
    private readonly string _dbPath;

    public TxFidelityPolicyStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-txpolicy-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private TxFidelityPolicyStore NewStore() =>
        new(NullLogger<TxFidelityPolicyStore>.Instance, _dbPath);

    [Fact]
    public void FirstRun_ReturnsStudioSsbPolicy()
    {
        using var store = NewStore();

        var policy = store.Get();

        Assert.Equal("studio-ssb", policy.ProfileId);
        Assert.Equal(55, policy.TargetSpectralDensity);
    }

    [Fact]
    public void SetThenGet_NormalizesProfileAndDensity()
    {
        using var store = NewStore();

        var saved = store.Set(new TxFidelityPolicyDto(" DX ", 120));
        var got = store.Get();

        Assert.Equal("dx", saved.ProfileId);
        Assert.Equal(100, saved.TargetSpectralDensity);
        Assert.Equal(saved, got);
    }

    [Fact]
    public void StatePersistsAcrossStoreInstances()
    {
        using (var first = NewStore())
        {
            first.Set(new TxFidelityPolicyDto("essb", 88));
        }

        using var second = NewStore();
        var policy = second.Get();

        Assert.Equal("essb", policy.ProfileId);
        Assert.Equal(88, policy.TargetSpectralDensity);
    }
}

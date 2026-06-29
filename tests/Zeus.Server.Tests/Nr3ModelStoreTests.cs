// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Exercises the NR3 model store's bundled-default fallback and operator-model
// override/revert behaviour. Each test uses an isolated tmp dir for the operator
// model store and a throw-away file standing in for the shipped default.
public class Nr3ModelStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _bundled;

    public Nr3ModelStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"zeus-nr3-{Guid.NewGuid():N}");
        _bundled = Path.Combine(Path.GetTempPath(), $"zeus-nr3-default-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(_bundled, Encoding.ASCII.GetBytes("DNNw-stand-in-default-model"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* test cleanup */ }
        try { if (File.Exists(_bundled)) File.Delete(_bundled); } catch { /* test cleanup */ }
    }

    private Nr3ModelStore NewStore(bool withDefault = true) =>
        new(NullLogger<Nr3ModelStore>.Instance, _dir, withDefault ? _bundled : null);

    [Fact]
    public void No_Default_And_No_Operator_Model_Is_Inert()
    {
        var store = NewStore(withDefault: false);
        Assert.Null(store.GetActiveModelPath());
        Assert.Null(store.GetActiveModelName());
        Assert.False(store.HasOperatorModel());
        Assert.False(store.UsingBundledDefault());
    }

    [Fact]
    public void Bundled_Default_Is_Active_When_No_Operator_Model()
    {
        var store = NewStore();
        Assert.Equal(_bundled, store.GetActiveModelPath());
        Assert.Equal(Nr3ModelStore.BundledDefaultDisplayName, store.GetActiveModelName());
        Assert.False(store.HasOperatorModel());
        Assert.True(store.UsingBundledDefault());
    }

    [Fact]
    public void Operator_Model_Overrides_The_Bundled_Default()
    {
        var store = NewStore();
        store.Install(Encoding.ASCII.GetBytes("operator-model-bytes"), "hf-voice.rnnn");

        Assert.True(store.HasOperatorModel());
        Assert.False(store.UsingBundledDefault());
        Assert.Equal("hf-voice.rnnn", store.GetActiveModelName());
        Assert.EndsWith("hf-voice.rnnn", store.GetActiveModelPath());
    }

    [Fact]
    public void Remove_Reverts_To_Bundled_Default_And_Fires_Changed_With_Its_Path()
    {
        var store = NewStore();
        store.Install(Encoding.ASCII.GetBytes("operator-model-bytes"), "hf-voice.rnnn");

        string? changedTo = "sentinel";
        store.Changed += p => changedTo = p;

        Assert.True(store.Remove());
        Assert.Equal(_bundled, changedTo); // reverted to default, not null/inert
        Assert.True(store.UsingBundledDefault());
        Assert.Equal(Nr3ModelStore.BundledDefaultDisplayName, store.GetActiveModelName());
    }

    [Fact]
    public void Remove_Without_Default_Goes_Inert_And_Fires_Changed_Null()
    {
        var store = NewStore(withDefault: false);
        store.Install(Encoding.ASCII.GetBytes("operator-model-bytes"), "hf-voice.rnnn");

        string? changedTo = "sentinel";
        store.Changed += p => changedTo = p;

        Assert.True(store.Remove());
        Assert.Null(changedTo);
        Assert.Null(store.GetActiveModelPath());
        Assert.False(store.UsingBundledDefault());
    }

    [Fact]
    public void Install_Rejects_Empty_Payload()
    {
        var store = NewStore();
        Assert.Throws<ArgumentException>(() => store.Install(ReadOnlySpan<byte>.Empty, "x.rnnn"));
    }
}

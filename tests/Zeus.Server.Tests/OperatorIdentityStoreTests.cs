// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Pins the shared operator-identity store + resolver: empty default (so the
// QRZ home station fallback engages), normalization on write, persistence across
// store instances (the desktop-restart bug), and the override → secondary → QRZ
// precedence used by every operator resolver.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class OperatorIdentityStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"zeus-operator-{Guid.NewGuid():N}");

    public OperatorIdentityStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private OperatorIdentityStore NewStore() =>
        new(NullLogger<OperatorIdentityStore>.Instance, Path.Combine(_root, "operator.db"));

    // Logged-out QRZ: GetStatus().Home is null, so the fallback path is exercised
    // without touching the network.
    private QrzService NewLoggedOutQrz() =>
        new(new SingleClientFactory(), NullLogger<QrzService>.Instance,
            new CredentialStore(NullLogger<CredentialStore>.Instance, Path.Combine(_root, "creds.db")));

    [Fact]
    public void Default_Is_Empty_Override()
    {
        using var store = NewStore();
        var id = store.Get();
        Assert.Equal("", id.Callsign);
        Assert.Equal("", id.Grid);
        Assert.False(id.IsComplete);
    }

    [Fact]
    public void Set_Normalizes_Call_And_Grid()
    {
        using var store = NewStore();
        var saved = store.Set(new OperatorIdentity("  w1aw  ", "  fn31pr  "));
        Assert.Equal("W1AW", saved.Callsign);
        Assert.Equal("FN31PR", saved.Grid); // upper-cased, capped at 6
        Assert.True(saved.IsComplete);
    }

    [Fact]
    public void Set_Drops_Non_Maidenhead_Grid()
    {
        using var store = NewStore();
        var saved = store.Set(new OperatorIdentity("W1AW", "12FN")); // not letter-letter
        Assert.Equal("", saved.Grid);
    }

    [Fact]
    public void Set_Persists_Across_Store_Instances()
    {
        using (var store = NewStore())
            store.Set(new OperatorIdentity("K1ABC", "FN42"));

        using var reopened = NewStore();
        var id = reopened.Get();
        Assert.Equal("K1ABC", id.Callsign);
        Assert.Equal("FN42", id.Grid);
    }

    [Fact]
    public void Resolver_Override_Wins_Over_Secondary()
    {
        using var store = NewStore();
        store.Set(new OperatorIdentity("W1AW", "FN31"));
        var qrz = NewLoggedOutQrz();

        var (call, grid) = OperatorIdentityResolver.Resolve(store, qrz, "K1ABC", "EM12");
        Assert.Equal("W1AW", call);
        Assert.Equal("FN31", grid);
    }

    [Fact]
    public void Resolver_Falls_Back_To_Secondary_When_Override_Blank()
    {
        using var store = NewStore(); // empty override
        var qrz = NewLoggedOutQrz();

        var (call, grid) = OperatorIdentityResolver.Resolve(store, qrz, "k1abc", "fn42");
        Assert.Equal("K1ABC", call); // secondary normalized
        Assert.Equal("FN42", grid);
    }

    [Fact]
    public void Resolver_Blank_When_No_Source()
    {
        using var store = NewStore();
        var qrz = NewLoggedOutQrz();

        var (call, grid) = OperatorIdentityResolver.Resolve(store, qrz);
        Assert.Equal("", call);
        Assert.Equal("", grid);
    }

    [Fact]
    public void Status_Reports_Override_Without_Qrz_Flags()
    {
        using var store = NewStore();
        store.Set(new OperatorIdentity("W1AW", "FN31"));
        var qrz = NewLoggedOutQrz();

        var status = OperatorIdentityResolver.Status(store, qrz);
        Assert.Equal("W1AW", status.Callsign);
        Assert.Equal("W1AW", status.ResolvedCallsign);
        Assert.False(status.CallsignFromQrz);
        Assert.False(status.GridFromQrz);
        Assert.True(status.IdentityResolved);
    }

    [Fact]
    public void Status_Unresolved_When_Empty_And_No_Qrz()
    {
        using var store = NewStore();
        var qrz = NewLoggedOutQrz();

        var status = OperatorIdentityResolver.Status(store, qrz);
        Assert.Equal("", status.ResolvedCallsign);
        Assert.False(status.IdentityResolved);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

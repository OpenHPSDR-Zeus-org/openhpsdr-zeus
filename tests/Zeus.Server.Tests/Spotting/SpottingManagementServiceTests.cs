// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Pins the safety-critical invariants of the spotting config: BOTH uploaders are
// OFF by default (new network egress is opt-in), SetConfig normalises operator
// identity, the choice persists across store instances, and IdentityResolved is
// false until a callsign + grid exist.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests.Spotting;

public sealed class SpottingManagementServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"zeus-spotting-{Guid.NewGuid():N}");

    public SpottingManagementServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private SpottingSettingsStore NewStore() =>
        new(NullLogger<SpottingSettingsStore>.Instance, Path.Combine(_root, "spotting.db"));

    // A fresh, logged-out QRZ service: GetStatus().Home is null, so ResolveOperator
    // falls through to the override only (no network ever touched).
    private QrzService NewLoggedOutQrz() =>
        new(new SingleClientFactory(), NullLogger<QrzService>.Instance,
            new CredentialStore(NullLogger<CredentialStore>.Instance, Path.Combine(_root, "creds.db")));

    private OperatorIdentityStore NewIdentityStore() =>
        new(NullLogger<OperatorIdentityStore>.Instance, Path.Combine(_root, "operator.db"));

    private SpottingManagementService NewService(SpottingSettingsStore store) =>
        new(NullLogger<SpottingManagementService>.Instance, store, NewIdentityStore(), NewLoggedOutQrz());

    private SpottingManagementService NewService(SpottingSettingsStore store, OperatorIdentityStore identity) =>
        new(NullLogger<SpottingManagementService>.Instance, store, identity, NewLoggedOutQrz());

    [Fact]
    public void Defaults_Both_Uploaders_Disabled_When_Nothing_Persisted()
    {
        using var store = NewStore();
        var svc = NewService(store);

        var cfg = svc.GetConfig();
        Assert.False(cfg.PskReporterEnabled); // opt-in egress, off by default
        Assert.False(cfg.WsprnetEnabled);
        Assert.Equal("", cfg.Callsign);
        Assert.Equal("", cfg.Grid);

        var status = svc.GetStatus();
        Assert.False(status.PskReporterEnabled);
        Assert.False(status.WsprnetEnabled);
        Assert.False(status.IdentityResolved); // no call/grid yet
    }

    [Fact]
    public void SetConfig_Normalizes_Call_And_Grid()
    {
        using var store = NewStore();
        var svc = NewService(store);

        var status = svc.SetConfig(new SpottingRuntimeConfig(
            PskReporterEnabled: true, WsprnetEnabled: false,
            Callsign: "  k1abc  ", Grid: "  fn42aa  "));

        Assert.Equal("K1ABC", status.Callsign);   // upper-cased + trimmed
        Assert.Equal("FN42AA", status.Grid);      // upper-cased + trimmed (<=6)
        Assert.True(status.PskReporterEnabled);
        Assert.True(status.IdentityResolved);
    }

    [Fact]
    public void SetConfig_Truncates_Grid_To_Six_Chars()
    {
        using var store = NewStore();
        var svc = NewService(store);

        var status = svc.SetConfig(new SpottingRuntimeConfig(Grid: "FN42AA99"));
        Assert.Equal("FN42AA", status.Grid);
    }

    [Fact]
    public void IdentityResolved_False_When_Only_Call_Set()
    {
        using var store = NewStore();
        var svc = NewService(store);

        var status = svc.SetConfig(new SpottingRuntimeConfig(Callsign: "K1ABC", Grid: ""));
        Assert.False(status.IdentityResolved);
    }

    [Fact]
    public void SetConfig_Persists_Across_Store_Instances()
    {
        using (var store = NewStore())
        {
            var svc = NewService(store);
            svc.SetConfig(new SpottingRuntimeConfig(
                PskReporterEnabled: true, WsprnetEnabled: true,
                Callsign: "K1ABC", Grid: "FN42"));
        }

        using var reopened = NewStore();
        var reloaded = NewService(reopened).GetConfig();
        Assert.True(reloaded.PskReporterEnabled);
        Assert.True(reloaded.WsprnetEnabled);
        Assert.Equal("K1ABC", reloaded.Callsign);
        Assert.Equal("FN42", reloaded.Grid);
    }

    [Fact]
    public void ResolveOperator_Returns_Override_When_Set()
    {
        using var store = NewStore();
        var svc = NewService(store);
        svc.SetConfig(new SpottingRuntimeConfig(Callsign: "K1ABC", Grid: "FN42"));

        var (call, grid) = svc.ResolveOperator();
        Assert.Equal("K1ABC", call);
        Assert.Equal("FN42", grid);
    }

    [Fact]
    public void ResolveOperator_Blank_When_No_Override_And_No_Qrz_Home()
    {
        using var store = NewStore();
        var svc = NewService(store);

        var (call, grid) = svc.ResolveOperator();
        Assert.Equal("", call);
        Assert.Equal("", grid);
    }

    [Fact]
    public void ResolveOperator_Shared_Identity_Wins_Over_Local_Config()
    {
        using var store = NewStore();
        using var identity = NewIdentityStore();
        identity.Set(new OperatorIdentity("W1AW", "FN31"));

        var svc = NewService(store, identity);
        // A different legacy/secondary call in the local spotting config must NOT
        // override the shared identity.
        svc.SetConfig(new SpottingRuntimeConfig(Callsign: "K1ABC", Grid: "EM12"));

        var (call, grid) = svc.ResolveOperator();
        Assert.Equal("W1AW", call);
        Assert.Equal("FN31", grid);
    }

    [Fact]
    public void ResolveOperator_Falls_Back_To_Local_Config_When_Shared_Blank()
    {
        using var store = NewStore();
        using var identity = NewIdentityStore(); // empty shared override
        var svc = NewService(store, identity);
        svc.SetConfig(new SpottingRuntimeConfig(Callsign: "K1ABC", Grid: "FN42"));

        var (call, grid) = svc.ResolveOperator();
        Assert.Equal("K1ABC", call);
        Assert.Equal("FN42", grid);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

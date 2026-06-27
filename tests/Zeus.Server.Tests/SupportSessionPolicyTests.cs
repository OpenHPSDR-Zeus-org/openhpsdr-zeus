// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Server.Hosting.Support;

namespace Zeus.Server.Tests;

/// <summary>
/// The read-only allowlist is the safety core of a maintainer support session, so
/// it gets exhaustive coverage: every mutating method refused, only the named
/// diagnostic surfaces readable, and nothing smuggleable past via query strings.
/// </summary>
public sealed class SupportSessionPolicyTests
{
    [Theory]
    [InlineData("/api/diagnostics")]
    [InlineData("/api/diagnostics/v2")]
    [InlineData("/api/version")]
    [InlineData("/api/capabilities")]
    [InlineData("/api/system/update")]
    [InlineData("/api/state")]
    public void Get_OnAllowlistedPath_IsAllowed(string path)
    {
        Assert.True(SupportSessionPolicy.IsAllowed("GET", path));
        Assert.True(SupportSessionPolicy.IsAllowed("HEAD", path));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public void MutatingMethod_OnAllowlistedPath_IsDenied(string method)
    {
        // Even on an otherwise-readable path, no mutating method is ever allowed.
        Assert.False(SupportSessionPolicy.IsAllowed(method, "/api/state"));
        Assert.False(SupportSessionPolicy.IsReadOnlyMethod(method));
    }

    [Theory]
    [InlineData("/api/prefs/databases")]
    [InlineData("/api/log/export")]
    [InlineData("/api/log")]
    [InlineData("/api/qrz")]
    [InlineData("/api/chat")]
    [InlineData("/api/tx/ps")]
    [InlineData("/api/remote/password")]
    [InlineData("/api/support/approve")]
    [InlineData("/api/audio/native")]
    [InlineData("/")]
    [InlineData("/api")]
    public void Get_OnNonAllowlistedPath_IsDenied(string path)
    {
        Assert.False(SupportSessionPolicy.IsAllowed("GET", path));
        Assert.False(SupportSessionPolicy.IsAllowedPath(path));
    }

    [Fact]
    public void QueryString_IsStrippedBeforeMatching()
    {
        // A query on an allowlisted path doesn't change the verdict…
        Assert.True(SupportSessionPolicy.IsAllowed("GET", "/api/state?x=1"));
        // …and an allowlisted-looking query on a NON-allowlisted path cannot smuggle it through.
        Assert.False(SupportSessionPolicy.IsAllowed("GET", "/api/prefs?x=/api/state"));
    }

    [Fact]
    public void Matching_IsCaseInsensitive()
    {
        Assert.True(SupportSessionPolicy.IsAllowed("get", "/API/STATE"));
        Assert.True(SupportSessionPolicy.IsReadOnlyMethod("Head"));
    }

    [Theory]
    [InlineData(null, "/api/state")]
    [InlineData("GET", null)]
    [InlineData("", "/api/state")]
    [InlineData("GET", "")]
    public void NullOrEmptyInput_IsDenied(string? method, string? path)
    {
        Assert.False(SupportSessionPolicy.IsAllowed(method, path));
    }
}

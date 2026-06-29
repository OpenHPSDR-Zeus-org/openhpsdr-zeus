// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using Zeus.Server.DxCluster;

namespace Zeus.Server.Tests;

public class DxClusterHandshakeTests
{
    [Fact]
    public void SendsCallsign_OnLoginPrompt()
    {
        var hs = new DxClusterHandshake("K1ABC", password: null, loginCommands: null);
        Assert.Empty(hs.OnLine("Welcome to the cluster"));
        var r = hs.OnLine("Please enter your call: ");
        Assert.Equal(new[] { "K1ABC" }, r);
        Assert.True(hs.CallsignSent);
    }

    [Theory]
    [InlineData("login: ")]
    [InlineData("Please enter your call:")]
    [InlineData("enter your call")]
    [InlineData("callsign:")]
    [InlineData("Your call?")]
    public void RecognisesCallPromptVariants(string prompt)
    {
        var hs = new DxClusterHandshake("K1ABC", null, null);
        Assert.Equal(new[] { "K1ABC" }, hs.OnLine(prompt));
    }

    [Fact]
    public void DoesNotResendCallsign()
    {
        var hs = new DxClusterHandshake("K1ABC", null, null);
        Assert.Single(hs.OnLine("login:"));
        // A second prompt must NOT re-send the callsign.
        Assert.Empty(hs.OnLine("login:"));
    }

    [Fact]
    public void SendsPassword_OnlyWhenConfiguredAndPrompted()
    {
        var hs = new DxClusterHandshake("K1ABC", "secret", null);
        Assert.Equal(new[] { "K1ABC" }, hs.OnLine("login:"));
        Assert.Equal(new[] { "secret" }, hs.OnLine("password: "));
        // Idempotent.
        Assert.Empty(hs.OnLine("password: "));
    }

    [Fact]
    public void NoPasswordConfigured_PasswordPromptIgnored()
    {
        var hs = new DxClusterHandshake("K1ABC", password: null, loginCommands: null);
        Assert.Equal(new[] { "K1ABC" }, hs.OnLine("login:"));
        // Without a configured password we never send one even if prompted.
        Assert.Empty(hs.OnLine("password: "));
    }

    [Fact]
    public void SendsLoginCommands_OnceAfterLogin_NoPassword()
    {
        var hs = new DxClusterHandshake("K1ABC", null, new[] { "set/filter on", "sh/dx" });
        Assert.Equal(new[] { "K1ABC" }, hs.OnLine("login:"));
        // First post-login line triggers the configured commands, once.
        Assert.Equal(new[] { "set/filter on", "sh/dx" }, hs.OnLine("K1ABC de NODE >"));
        Assert.Empty(hs.OnLine("another line"));
    }

    [Fact]
    public void SendsLoginCommands_OnlyAfterPassword_WhenPasswordRequired()
    {
        var hs = new DxClusterHandshake("K1ABC", "secret", new[] { "sh/dx" });
        Assert.Equal(new[] { "K1ABC" }, hs.OnLine("login:"));
        // Login commands must NOT fire before the password is sent.
        Assert.Equal(new[] { "secret" }, hs.OnLine("password:"));
        Assert.Equal(new[] { "sh/dx" }, hs.OnLine("node ready"));
    }

    [Fact]
    public void EmptyCallsign_NeverSends()
    {
        var hs = new DxClusterHandshake("", null, null);
        Assert.Empty(hs.OnLine("login:"));
        Assert.False(hs.CallsignSent);
    }

    [Fact]
    public void BlankLoginCommands_AreFilteredOut()
    {
        var hs = new DxClusterHandshake("K1ABC", null, new[] { "  ", "", "sh/dx", "  " });
        hs.OnLine("login:");
        Assert.Equal(new[] { "sh/dx" }, hs.OnLine("post-login"));
    }
}

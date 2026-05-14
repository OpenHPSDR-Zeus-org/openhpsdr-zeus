// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Zeus.Protocol2.Tests;

/// <summary>
/// Coverage for the helpers underpinning the macOS UDP route-priming
/// workaround (issue #171, Brick2 ARP quirk).
///
/// The priming SendTo can't be unit-tested without a real radio on the LAN,
/// but the interface-lookup helper and the never-throws contract of
/// <see cref="Protocol2Client.PrimeMacOSUdpRoute"/> both have deterministic
/// fingerprints we can pin in CI.
/// </summary>
public class MacOSUdpPrimeTests
{
    [Fact]
    public void FindInterfaceIndexForLocalAddress_ReturnsZero_ForUnknownAddress()
    {
        // 0.0.0.0 is not owned by any NIC, so the lookup must fall through
        // and the caller will skip priming. This is the "safe-to-call-anywhere"
        // contract — never throw on an address the host doesn't recognise.
        Assert.Equal(0, Protocol2Client.FindInterfaceIndexForLocalAddress(IPAddress.Any));
    }

    [Fact]
    public void FindInterfaceIndexForLocalAddress_FindsLoopback_WhenPresent()
    {
        // Skip in the rare CI environment without a loopback interface; the
        // assertion would have nothing to compare against. Every developer
        // machine and every standard CI runner has at least one loopback NIC.
        var loopback = FindLoopbackInterfaceIndex();
        if (loopback <= 0) return; // host doesn't expose loopback IPv4

        Assert.Equal(loopback, Protocol2Client.FindInterfaceIndexForLocalAddress(IPAddress.Loopback));
    }

    [Fact]
    public void PrimeMacOSUdpRoute_NeverThrows_ForUnreachableAddress()
    {
        // Calling priming with a TEST-NET-1 address (RFC 5737, guaranteed
        // unroutable) on any host must produce a clean no-throw. The
        // priming is best-effort — every failure path is swallowed by
        // design because the regular SendCmdGeneral that follows will do
        // ARP eventually on hosts that don't need the workaround.
        var doc = IPAddress.Parse("192.0.2.1");
        var ex = Record.Exception(() =>
            Protocol2Client.PrimeMacOSUdpRoute(doc, NullLogger.Instance));
        Assert.Null(ex);
    }

    private static int FindLoopbackInterfaceIndex()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType != NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.Equals(IPAddress.Loopback))
                {
                    var ipv4 = nic.GetIPProperties().GetIPv4Properties();
                    return ipv4?.Index ?? 0;
                }
            }
        }
        return 0;
    }
}

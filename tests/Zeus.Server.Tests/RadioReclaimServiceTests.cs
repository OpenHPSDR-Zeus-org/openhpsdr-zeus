// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Xunit;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Pins the wire frames <see cref="RadioReclaimService"/> sends to free a
/// Busy radio. Getting these wrong would either fail to drop the current
/// owner or send the radio garbage, so the bytes are asserted directly.
/// </summary>
public class RadioReclaimServiceTests
{
    [Fact]
    public void Protocol1StopFrame_IsMetisStopCommand()
    {
        var frame = RadioReclaimService.BuildProtocol1StopFrame();

        // Metis start/stop command: EF FE 04 00 (00 = stop). 64-byte datagram.
        Assert.Equal(64, frame.Length);
        Assert.Equal(0xEF, frame[0]);
        Assert.Equal(0xFE, frame[1]);
        Assert.Equal(0x04, frame[2]);
        Assert.Equal(0x00, frame[3]); // run bit cleared == stop
        for (int i = 4; i < frame.Length; i++)
            Assert.Equal(0x00, frame[i]);
    }

    [Fact]
    public void Protocol2StopFrame_IsAllZeroHighPriorityPacket()
    {
        var frame = RadioReclaimService.BuildProtocol2StopFrame();

        // P2 high-priority "to radio" buffer (matches Protocol2Client.BufLen).
        // All zero == sequence 0 and run/PTT cleared at byte 4: the radio
        // stops streaming to its current owner.
        Assert.Equal(1444, frame.Length);
        Assert.All(frame, b => Assert.Equal(0x00, b));
    }
}

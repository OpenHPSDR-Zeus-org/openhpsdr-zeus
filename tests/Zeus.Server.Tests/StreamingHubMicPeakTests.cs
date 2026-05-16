// SPDX-License-Identifier: GPL-2.0-or-later
//
// Smoke test for the new MicPeakFrame broadcast path. Phase 4 publishes a
// ~10 Hz mic-level telemetry frame from NativeMicCapture in desktop mode;
// this asserts the StreamingHub overload accepts the frame without throwing,
// both with zero connected clients (the common case at boot) and with a
// frame at the silence floor (which exercises the dBFS conversion edge).

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class StreamingHubMicPeakTests
{
    [Fact]
    public void Broadcast_MicPeakFrame_DoesNotThrow_NoClients()
    {
        // Audio worker thread will call this whether or not a client is
        // attached; the hub's early-return on _clients.IsEmpty is the
        // hot-path optimisation and must not allocate/serialise wastefully.
        // We can't easily observe the no-op from outside, but we *can*
        // assert that the call completes without an exception, which is
        // what the miniaudio callback depends on.
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var frame = new MicPeakFrame(PeakDbfs: -42.5f, TimestampUnixMs: 1_700_000_000_000L);
        var ex = Record.Exception(() => hub.Broadcast(in frame));
        Assert.Null(ex);
    }

    [Fact]
    public void Broadcast_MicPeakFrame_AcceptsSilenceFloor()
    {
        // The frame at the silence floor is what NativeMicCapture emits
        // when nothing is plugged in — it must not be rejected.
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var frame = new MicPeakFrame(PeakDbfs: MicPeakFrame.MinDbfs, TimestampUnixMs: 0L);
        var ex = Record.Exception(() => hub.Broadcast(in frame));
        Assert.Null(ex);
    }

    [Fact]
    public void Broadcast_MicPeakFrame_AcceptsZeroDbfs()
    {
        // Upper edge: 0 dBFS (clipping). Still must round-trip the hub.
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var frame = new MicPeakFrame(PeakDbfs: 0f, TimestampUnixMs: 1L);
        var ex = Record.Exception(() => hub.Broadcast(in frame));
        Assert.Null(ex);
    }
}

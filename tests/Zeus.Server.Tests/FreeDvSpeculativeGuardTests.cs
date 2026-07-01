// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Pins the engage/disengage guard for FreeDV's speculative parallel-detection
/// pool (<see cref="FreeDvService.ShouldSpeculate"/>). The pool must run ONLY in
/// the "unsynced, unlocked, receiving" window — exactly where racing every
/// candidate decoder beats the sequential dwell — and must retract the instant
/// the scanner camps a mode or the live decoder acquires sync (so we never spin
/// speculative decoders while a good mode is already locking). Pure logic: no
/// native modem, no real time, no async loop.
/// </summary>
public sealed class FreeDvSpeculativeGuardTests
{
    [Fact]
    public void Speculates_WhenScanningUnlockedAndUnsynced()
    {
        // The whole point: AUTO is scanning, nothing is camped, and the live
        // decoder hasn't synced — race the candidates.
        Assert.True(FreeDvService.ShouldSpeculate(scanning: true, scannerLocked: false, liveSynced: false));
    }

    [Fact]
    public void DoesNotSpeculate_WhenNotScanning()
    {
        // AUTO off / disengaged / transmitting all surface as scanning=false: no
        // pool (and byte-identical hot path).
        Assert.False(FreeDvService.ShouldSpeculate(scanning: false, scannerLocked: false, liveSynced: false));
        Assert.False(FreeDvService.ShouldSpeculate(scanning: false, scannerLocked: true, liveSynced: false));
        Assert.False(FreeDvService.ShouldSpeculate(scanning: false, scannerLocked: false, liveSynced: true));
    }

    [Fact]
    public void DoesNotSpeculate_WhenScannerLocked()
    {
        // Camped on a mode — the sequential lock authority owns it now; no race.
        Assert.False(FreeDvService.ShouldSpeculate(scanning: true, scannerLocked: true, liveSynced: false));
        Assert.False(FreeDvService.ShouldSpeculate(scanning: true, scannerLocked: true, liveSynced: true));
    }

    [Fact]
    public void DoesNotSpeculate_WhenLiveDecoderAlreadySynced()
    {
        // The current mode is already acquiring — let the scanner confirm/camp it
        // rather than burning cycles on parallel candidates.
        Assert.False(FreeDvService.ShouldSpeculate(scanning: true, scannerLocked: false, liveSynced: true));
    }
}

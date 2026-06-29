// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Unit-test the tuning invariants in <see cref="RadioService"/> after the
/// CTUN frozen-NCO model was reverted to classic "radio follows the dial":
///
///   1. <c>SetVfo</c> retunes the radio — <c>RadioLoHz</c> tracks the dial's
///      effective LO (== dial for SSB/AM/FM/DIG, dial ∓ pitch for CW) so RX
///      and TX both land on the dial. (Was frozen under the old CTUN model.)
///   2. <c>SetRadioLo</c> only moves the hardware NCO; <c>VfoHz</c> is untouched.
///   3. Out-of-range inputs to <c>SetRadioLo</c> clamp into the legal HPSDR
///      tunable range [0, 60_000_000] Hz (matching <c>SetVfo</c>); the
///      endpoint layer rejects with 400, but the service layer clamps for
///      safety so an internal caller can't park the NCO at a nonsense Hz.
///
/// We exercise the service directly rather than via the HTTP host — every
/// behaviour worth pinning lives in <see cref="RadioService"/> and the
/// service-level tests stay fast.
/// </summary>
public sealed class RadioServiceSetRadioLoTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PaSettingsStore _paStore;
    private readonly DspSettingsStore _dspStore;

    public RadioServiceSetRadioLoTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-lo-{Guid.NewGuid():N}.db");
        _paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        _dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath + ".dsp");
    }

    public void Dispose()
    {
        _paStore.Dispose();
        _dspStore.Dispose();
        foreach (var suffix in new[] { "", ".pa", ".dsp" })
        {
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); } catch { }
        }
    }

    private RadioService BuildRadio() =>
        new RadioService(NullLoggerFactory.Instance, _dspStore, _paStore);

    [Fact]
    public void SetRadioLo_InRange_UpdatesOnlyRadioLoHz()
    {
        using var radio = BuildRadio();
        // Park the dial somewhere far from the LO so the invariant is visible.
        var initial = radio.SetVfo(14_074_000);
        long dialBefore = initial.VfoHz;

        var after = radio.SetRadioLo(14_100_000);

        Assert.Equal(14_100_000, after.RadioLoHz);
        Assert.Equal(dialBefore, after.VfoHz);
    }

    [Fact]
    public void SetRadioLo_DoesNotMoveVfo_AcrossMultipleCalls()
    {
        using var radio = BuildRadio();
        var dial = radio.SetVfo(7_074_000).VfoHz;

        radio.SetRadioLo(7_000_000);
        radio.SetRadioLo(7_200_000);
        var snap = radio.SetRadioLo(7_300_000);

        Assert.Equal(dial, snap.VfoHz);
        Assert.Equal(7_300_000, snap.RadioLoHz);
    }

    [Fact]
    public void SetVfo_MovesRadioLoToTrackDial()
    {
        using var radio = BuildRadio();
        radio.SetRadioLo(28_400_000);

        var after = radio.SetVfo(28_410_500);

        // Classic "radio follows the dial" (CTUN frozen-NCO reverted): SetVfo
        // now retunes the LO to the dial's effective LO so RX and TX track the
        // dial, instead of leaving it frozen at the old centre (28_400_000).
        Assert.Equal(28_410_500, after.VfoHz);
        Assert.NotEqual(28_400_000, after.RadioLoHz);
        // RadioLoHz lands on the dial (± CW pitch, which is well under 2 kHz),
        // mode-agnostic so the test doesn't depend on the default RxMode.
        Assert.True(Math.Abs(after.RadioLoHz - 28_410_500) <= 2_000,
            $"RadioLoHz {after.RadioLoHz} should track dial 28410500 within CW pitch");
    }

    [Fact]
    public void SetRadioLo_NegativeInput_ClampsToZero()
    {
        using var radio = BuildRadio();
        var snap = radio.SetRadioLo(-1_000);
        Assert.Equal(0, snap.RadioLoHz);
    }

    [Fact]
    public void SetRadioLo_AboveMax_ClampsTo60MHz()
    {
        using var radio = BuildRadio();
        var snap = radio.SetRadioLo(75_000_000);
        Assert.Equal(60_000_000, snap.RadioLoHz);
    }
}

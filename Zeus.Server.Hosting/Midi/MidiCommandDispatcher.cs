// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.
//
// The supported-command case bodies are generated from the shared command
// catalogue so the enum, the UI catalogue, and this dispatch never drift.
// PureSignal arm is deliberately NOT part of this surface — no case keys PS.

using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Protocol1;

namespace Zeus.Server.Midi;

/// <summary>
/// Routes a resolved <see cref="ZeusMidiCommand"/> to the verified Zeus radio
/// seams (<see cref="RadioService"/> / <see cref="TxService"/>). Buttons pass a
/// note velocity in <paramref name="value"/> (0 = released); knobs/sliders pass
/// an absolute 0..127 in <paramref name="value"/>; wheels pass a signed encoder
/// step in <paramref name="delta"/>. Commands without a Zeus seam are
/// parity-only: they fall through to a Debug-level no-op so the full Thetis
/// surface stays selectable in the UI without ever throwing.
///
/// All TX keying flows through <see cref="TxService.TrySetMox"/> /
/// <c>TrySetTun</c> / <c>TrySetTwoTone</c> with <see cref="MoxSource.Midi"/>; a
/// MIDI button never arms PureSignal and only keys on an explicit press.
/// </summary>
public sealed class MidiCommandDispatcher
{
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly MidiDispatchState _state;
    private readonly ILogger<MidiCommandDispatcher> _log;

    public MidiCommandDispatcher(
        RadioService radio,
        TxService tx,
        MidiDispatchState state,
        ILogger<MidiCommandDispatcher>? log = null)
    {
        _radio = radio;
        _tx = tx;
        _state = state;
        _log = log ?? NullLogger<MidiCommandDispatcher>.Instance;
    }

    /// <summary>The engine-local dispatch state (toggles, wheel sensitivity).</summary>
    public MidiDispatchState DispatchState => _state;

    /// <summary>Route one resolved command. Never throws — a faulty seam call is
    /// logged and swallowed so one bad mapping can't take down the MIDI thread.</summary>
    public void Dispatch(ZeusMidiCommand cmd, int value, int delta)
    {
        var r = _radio;
        var tx = _tx;
        var st = _state;
        try
        {
            switch (cmd)
            {
            case ZeusMidiCommand.VfoAtoB:
            {
                if (value > 0) { var s = st.Snapshot(); r.SetReceiverVfo(1, s.VfoHz); r.SetMode(s.Mode, TxVfo.B); }
                break;
            }
            case ZeusMidiCommand.VfoBtoA:
            {
                if (value > 0) { var s = st.Snapshot(); var b = s.Rx2(); r.SetReceiverVfo(0, b.VfoHz); r.SetMode(b.Mode, TxVfo.A); }
                break;
            }
            case ZeusMidiCommand.VfoSwap:
            {
                if (value > 0) r.SwapVfos();
                break;
            }
            case ZeusMidiCommand.RitOnOff:
            {
                if (value > 0) r.SetRit(!st.Snapshot().RitEnabled, null);
                break;
            }
            case ZeusMidiCommand.XitOnOff:
            {
                if (value > 0) r.SetXit(!st.Snapshot().XitEnabled, null);
                break;
            }
            case ZeusMidiCommand.RIT_clear:
            {
                if (value > 0) r.SetRit(null, 0L);
                break;
            }
            case ZeusMidiCommand.XIT_clear:
            {
                if (value > 0) r.SetXit(null, 0L);
                break;
            }
            case ZeusMidiCommand.RIT_inc:
            {
                r.SetRit(null, st.Snapshot().RitHz + (long)delta * 10);
                break;
            }
            case ZeusMidiCommand.XIT_inc:
            {
                r.SetXit(null, st.Snapshot().XitHz + (long)delta * 10);
                break;
            }
            case ZeusMidiCommand.BandUp:
            {
                if (value > 0) { var bands = new[] { "160m","80m","60m","40m","30m","20m","17m","15m","12m","10m","6m" }; var centers = new long[] { 1_900_000,3_750_000,5_357_000,7_175_000,10_125_000,14_175_000,18_120_000,21_300_000,24_940_000,28_400_000,50_150_000 }; int i = System.Array.IndexOf(bands, BandUtils.FreqToBand(st.Snapshot().VfoHz)); int ni = System.Math.Clamp((i < 0 ? -1 : i) + 1, 0, bands.Length - 1); r.SetReceiverVfo(0, centers[ni]); }
                break;
            }
            case ZeusMidiCommand.BandDown:
            {
                if (value > 0) { var bands = new[] { "160m","80m","60m","40m","30m","20m","17m","15m","12m","10m","6m" }; var centers = new long[] { 1_900_000,3_750_000,5_357_000,7_175_000,10_125_000,14_175_000,18_120_000,21_300_000,24_940_000,28_400_000,50_150_000 }; int i = System.Array.IndexOf(bands, BandUtils.FreqToBand(st.Snapshot().VfoHz)); int ni = System.Math.Clamp((i < 0 ? bands.Length : i) - 1, 0, bands.Length - 1); r.SetReceiverVfo(0, centers[ni]); }
                break;
            }
            case ZeusMidiCommand.Band160m:
            {
                if (value > 0) r.SetReceiverVfo(0, 1_900_000);
                break;
            }
            case ZeusMidiCommand.Band80m:
            {
                if (value > 0) r.SetReceiverVfo(0, 3_750_000);
                break;
            }
            case ZeusMidiCommand.Band60m:
            {
                if (value > 0) r.SetReceiverVfo(0, 5_357_000);
                break;
            }
            case ZeusMidiCommand.Band40m:
            {
                if (value > 0) r.SetReceiverVfo(0, 7_175_000);
                break;
            }
            case ZeusMidiCommand.Band30m:
            {
                if (value > 0) r.SetReceiverVfo(0, 10_125_000);
                break;
            }
            case ZeusMidiCommand.Band20m:
            {
                if (value > 0) r.SetReceiverVfo(0, 14_175_000);
                break;
            }
            case ZeusMidiCommand.Band17m:
            {
                if (value > 0) r.SetReceiverVfo(0, 18_120_000);
                break;
            }
            case ZeusMidiCommand.Band15m:
            {
                if (value > 0) r.SetReceiverVfo(0, 21_300_000);
                break;
            }
            case ZeusMidiCommand.Band12m:
            {
                if (value > 0) r.SetReceiverVfo(0, 24_940_000);
                break;
            }
            case ZeusMidiCommand.Band10m:
            {
                if (value > 0) r.SetReceiverVfo(0, 28_400_000);
                break;
            }
            case ZeusMidiCommand.Band6m:
            {
                if (value > 0) r.SetReceiverVfo(0, 50_150_000);
                break;
            }
            case ZeusMidiCommand.Band160mRX2:
            {
                if (value > 0) r.SetReceiverVfo(1, 1_900_000);
                break;
            }
            case ZeusMidiCommand.Band80mRX2:
            {
                if (value > 0) r.SetReceiverVfo(1, 3_750_000);
                break;
            }
            case ZeusMidiCommand.Band60mRX2:
            {
                if (value > 0) r.SetReceiverVfo(1, 5_357_000);
                break;
            }
            case ZeusMidiCommand.Band40mRX2:
            {
                if (value > 0) r.SetReceiverVfo(1, 7_175_000);
                break;
            }
            case ZeusMidiCommand.Band30mRX2:
            {
                if (value > 0) r.SetReceiverVfo(1, 10_125_000);
                break;
            }
            case ZeusMidiCommand.Band20mRX2:
            {
                if (value > 0) r.SetReceiverVfo(1, 14_175_000);
                break;
            }
            case ZeusMidiCommand.Band17mRX2:
            {
                if (value > 0) r.SetReceiverVfo(1, 18_120_000);
                break;
            }
            case ZeusMidiCommand.Band15mRX2:
            {
                if (value > 0) r.SetReceiverVfo(1, 21_300_000);
                break;
            }
            case ZeusMidiCommand.Band12mRX2:
            {
                if (value > 0) r.SetReceiverVfo(1, 24_940_000);
                break;
            }
            case ZeusMidiCommand.Band10mRX2:
            {
                if (value > 0) r.SetReceiverVfo(1, 28_400_000);
                break;
            }
            case ZeusMidiCommand.Band6mRX2:
            {
                if (value > 0) r.SetReceiverVfo(1, 50_150_000);
                break;
            }
            case ZeusMidiCommand.Rx2BandUp:
            {
                if (value > 0) { var bands = new[] { "160m","80m","60m","40m","30m","20m","17m","15m","12m","10m","6m" }; var centers = new long[] { 1_900_000,3_750_000,5_357_000,7_175_000,10_125_000,14_175_000,18_120_000,21_300_000,24_940_000,28_400_000,50_150_000 }; int i = System.Array.IndexOf(bands, BandUtils.FreqToBand(st.Snapshot().Rx2().VfoHz)); int ni = System.Math.Clamp((i < 0 ? -1 : i) + 1, 0, bands.Length - 1); r.SetReceiverVfo(1, centers[ni]); }
                break;
            }
            case ZeusMidiCommand.Rx2BandDown:
            {
                if (value > 0) { var bands = new[] { "160m","80m","60m","40m","30m","20m","17m","15m","12m","10m","6m" }; var centers = new long[] { 1_900_000,3_750_000,5_357_000,7_175_000,10_125_000,14_175_000,18_120_000,21_300_000,24_940_000,28_400_000,50_150_000 }; int i = System.Array.IndexOf(bands, BandUtils.FreqToBand(st.Snapshot().Rx2().VfoHz)); int ni = System.Math.Clamp((i < 0 ? bands.Length : i) - 1, 0, bands.Length - 1); r.SetReceiverVfo(1, centers[ni]); }
                break;
            }
            case ZeusMidiCommand.ChangeFreqVfoA:
            {
                r.SetReceiverVfo(0, st.Snapshot().VfoHz + (long)delta * 10);
                break;
            }
            case ZeusMidiCommand.ChangeFreqVfoB:
            {
                r.SetReceiverVfo(1, st.Snapshot().Rx2().VfoHz + (long)delta * 10);
                break;
            }
            case ZeusMidiCommand.MoveVFOADown100Khz:
            {
                if (value > 0) r.SetReceiverVfo(0, st.Snapshot().VfoHz - 100_000);
                break;
            }
            case ZeusMidiCommand.MoveVFOAUp100Khz:
            {
                if (value > 0) r.SetReceiverVfo(0, st.Snapshot().VfoHz + 100_000);
                break;
            }
            case ZeusMidiCommand.MoveVFOBDown100Khz:
            {
                if (value > 0) r.SetReceiverVfo(1, st.Snapshot().Rx2().VfoHz - 100_000);
                break;
            }
            case ZeusMidiCommand.MoveVFOBUp100Khz:
            {
                if (value > 0) r.SetReceiverVfo(1, st.Snapshot().Rx2().VfoHz + 100_000);
                break;
            }
            case ZeusMidiCommand.CTunOnOff:
            {
                if (value > 0) r.SetCtunEnabled(!st.Snapshot().CtunEnabled);
                break;
            }
            case ZeusMidiCommand.LockVFOOnOff:
            {
                if (value > 0) r.SetVfoLock(!st.Snapshot().VfoLocked);
                break;
            }
            case ZeusMidiCommand.LockVFOAOnOff:
            {
                if (value > 0) r.SetVfoLock(!st.Snapshot().VfoLocked);
                break;
            }
            case ZeusMidiCommand.MultiStepVfoA:
            {
                r.SetReceiverVfo(0, st.Snapshot().VfoHz + (long)delta * 1000);
                break;
            }
            case ZeusMidiCommand.Rx1ModeNext:
            {
                if (value > 0) { var s = st.Snapshot(); var modes = System.Enum.GetValues<RxMode>(); int i = System.Array.IndexOf(modes, s.Mode); r.SetMode(modes[((i + 1) % modes.Length + modes.Length) % modes.Length], TxVfo.A); }
                break;
            }
            case ZeusMidiCommand.Rx1ModePrev:
            {
                if (value > 0) { var s = st.Snapshot(); var modes = System.Enum.GetValues<RxMode>(); int i = System.Array.IndexOf(modes, s.Mode); r.SetMode(modes[((i - 1) % modes.Length + modes.Length) % modes.Length], TxVfo.A); }
                break;
            }
            case ZeusMidiCommand.Rx2ModeNext:
            {
                if (value > 0 && st.Snapshot() is { Receivers: { Count: > 1 } } s) { var modes = System.Enum.GetValues<RxMode>(); int i = System.Array.IndexOf(modes, s.Receivers[1].Mode); r.SetMode(modes[((i + 1) % modes.Length + modes.Length) % modes.Length], TxVfo.B); }
                break;
            }
            case ZeusMidiCommand.Rx2ModePrev:
            {
                if (value > 0 && st.Snapshot() is { Receivers: { Count: > 1 } } s) { var modes = System.Enum.GetValues<RxMode>(); int i = System.Array.IndexOf(modes, s.Receivers[1].Mode); r.SetMode(modes[((i - 1) % modes.Length + modes.Length) % modes.Length], TxVfo.B); }
                break;
            }
            case ZeusMidiCommand.ModeSSB:
            {
                if (value > 0) { var s = st.Snapshot(); r.SetMode(s.VfoHz < 10_000_000 ? RxMode.LSB : RxMode.USB, TxVfo.A); }
                break;
            }
            case ZeusMidiCommand.ModeLSB:
            {
                if (value > 0) r.SetMode(RxMode.LSB, TxVfo.A);
                break;
            }
            case ZeusMidiCommand.ModeUSB:
            {
                if (value > 0) r.SetMode(RxMode.USB, TxVfo.A);
                break;
            }
            case ZeusMidiCommand.ModeDSB:
            {
                if (value > 0) r.SetMode(RxMode.DSB, TxVfo.A);
                break;
            }
            case ZeusMidiCommand.ModeCW:
            {
                if (value > 0) r.SetMode(RxMode.CWU, TxVfo.A);
                break;
            }
            case ZeusMidiCommand.ModeCWL:
            {
                if (value > 0) r.SetMode(RxMode.CWL, TxVfo.A);
                break;
            }
            case ZeusMidiCommand.ModeCWU:
            {
                if (value > 0) r.SetMode(RxMode.CWU, TxVfo.A);
                break;
            }
            case ZeusMidiCommand.ModeFM:
            {
                if (value > 0) r.SetMode(RxMode.FM, TxVfo.A);
                break;
            }
            case ZeusMidiCommand.ModeAM:
            {
                if (value > 0) r.SetMode(RxMode.AM, TxVfo.A);
                break;
            }
            case ZeusMidiCommand.ModeDIGU:
            {
                if (value > 0) r.SetMode(RxMode.DIGU, TxVfo.A);
                break;
            }
            case ZeusMidiCommand.ModeDIGL:
            {
                if (value > 0) r.SetMode(RxMode.DIGL, TxVfo.A);
                break;
            }
            case ZeusMidiCommand.ModeSAM:
            {
                if (value > 0) r.SetMode(RxMode.SAM, TxVfo.A);
                break;
            }
            case ZeusMidiCommand.Rx1FilterWider:
            {
                if (value > 0) { var s = st.Snapshot(); int c = (s.FilterLowHz + s.FilterHighHz) / 2, half = (s.FilterHighHz - s.FilterLowHz) / 2 + 50; r.SetFilter(c - half, c + half); }
                break;
            }
            case ZeusMidiCommand.Rx1FilterNarrower:
            {
                if (value > 0) { var s = st.Snapshot(); int c = (s.FilterLowHz + s.FilterHighHz) / 2, half = (s.FilterHighHz - s.FilterLowHz) / 2 - 50; r.SetFilter(c - half, c + half); }
                break;
            }
            case ZeusMidiCommand.Rx2FilterWider:
            {
                if (value > 0 && st.Snapshot() is { Receivers: { Count: > 1 } } s) { var rx = s.Receivers[1]; int c = (rx.FilterLowHz + rx.FilterHighHz) / 2, half = (rx.FilterHighHz - rx.FilterLowHz) / 2 + 50; r.SetFilter(c - half, c + half, null, TxVfo.B); }
                break;
            }
            case ZeusMidiCommand.Rx2FilterNarrower:
            {
                if (value > 0 && st.Snapshot() is { Receivers: { Count: > 1 } } s) { var rx = s.Receivers[1]; int c = (rx.FilterLowHz + rx.FilterHighHz) / 2, half = (rx.FilterHighHz - rx.FilterLowHz) / 2 - 50; r.SetFilter(c - half, c + half, null, TxVfo.B); }
                break;
            }
            case ZeusMidiCommand.FilterHigh:
            {
                if (delta != 0) { var s = st.Snapshot(); r.SetFilter(s.FilterLowHz, s.FilterHighHz + delta * 10); }
                break;
            }
            case ZeusMidiCommand.FilterLow:
            {
                if (delta != 0) { var s = st.Snapshot(); r.SetFilter(s.FilterLowHz + delta * 10, s.FilterHighHz); }
                break;
            }
            case ZeusMidiCommand.FilterShift:
            {
                if (delta != 0) { var s = st.Snapshot(); int sh = delta * 10; r.SetFilter(s.FilterLowHz + sh, s.FilterHighHz + sh); }
                break;
            }
            case ZeusMidiCommand.FilterBandwidth:
            {
                if (delta != 0) { var s = st.Snapshot(); int c = (s.FilterLowHz + s.FilterHighHz) / 2, half = (s.FilterHighHz - s.FilterLowHz) / 2 + delta * 10; r.SetFilter(c - half, c + half); }
                break;
            }
            case ZeusMidiCommand.TxFilterHigh:
            {
                if (delta != 0) { var s = st.Snapshot(); r.SetTxFilter(s.TxFilterLowHz, s.TxFilterHighHz + delta * 10); }
                break;
            }
            case ZeusMidiCommand.TxFilterLow:
            {
                if (delta != 0) { var s = st.Snapshot(); r.SetTxFilter(s.TxFilterLowHz + delta * 10, s.TxFilterHighHz); }
                break;
            }
            case ZeusMidiCommand.AgcModeUp:
            {
                if (value > 0) { var cur = st.Snapshot().Agc ?? new AgcConfig(); var next = (AgcMode)(((int)cur.Mode + 1) % 5); r.SetAgc(cur with { Mode = next }); }
                break;
            }
            case ZeusMidiCommand.AgcModeDown:
            {
                if (value > 0) { var cur = st.Snapshot().Agc ?? new AgcConfig(); var next = (AgcMode)(((int)cur.Mode - 1 + 5) % 5); r.SetAgc(cur with { Mode = next }); }
                break;
            }
            case ZeusMidiCommand.AgcModeKnob:
            {
                var cur = st.Snapshot().Agc ?? new AgcConfig(); int idx = Math.Clamp((int)Math.Round(st.Scale(value, 0, 4)), 0, 4); r.SetAgc(cur with { Mode = (AgcMode)idx });
                break;
            }
            case ZeusMidiCommand.Rx1AgcLevel:
            {
                r.SetAgcTop(st.Scale(value, 30.0, 90.0));
                break;
            }
            case ZeusMidiCommand.Rx1AgcLevelInc:
            {
                r.SetAgcTop(st.Snapshot().AgcTopDb + delta);
                break;
            }
            case ZeusMidiCommand.Nr1OnOff:
            {
                if (value > 0) { var cur = st.Snapshot().Nr ?? new NrConfig(); r.SetNr(cur with { NrMode = cur.NrMode == NrMode.Anr ? NrMode.Off : NrMode.Anr }); }
                break;
            }
            case ZeusMidiCommand.Nr2OnOff:
            {
                if (value > 0) { var cur = st.Snapshot().Nr ?? new NrConfig(); r.SetNr(cur with { NrMode = cur.NrMode == NrMode.Emnr ? NrMode.Off : NrMode.Emnr }); }
                break;
            }
            case ZeusMidiCommand.Nr3OnOff:
            {
                if (value > 0) { var cur = st.Snapshot().Nr ?? new NrConfig(); r.SetNr(cur with { NrMode = cur.NrMode == NrMode.Rnnr ? NrMode.Off : NrMode.Rnnr }); }
                break;
            }
            case ZeusMidiCommand.Nr4OnOff:
            {
                if (value > 0) { var cur = st.Snapshot().Nr ?? new NrConfig(); r.SetNr(cur with { NrMode = cur.NrMode == NrMode.Sbnr ? NrMode.Off : NrMode.Sbnr }); }
                break;
            }
            case ZeusMidiCommand.Nr4Amount:
            {
                var cur = st.Snapshot().Nr ?? new NrConfig(); r.SetNr(cur with { Nr4ReductionAmount = st.Scale(value, 0.0, 20.0) });
                break;
            }
            case ZeusMidiCommand.Rx1Nb1OnOff:
            {
                if (value > 0) { var cur = st.Snapshot().Nr ?? new NrConfig(); r.SetNr(cur with { NbMode = cur.NbMode == NbMode.Nb1 ? NbMode.Off : NbMode.Nb1 }); }
                break;
            }
            case ZeusMidiCommand.Rx1Nb2OnOff:
            {
                if (value > 0) { var cur = st.Snapshot().Nr ?? new NrConfig(); r.SetNr(cur with { NbMode = cur.NbMode == NbMode.Nb2 ? NbMode.Off : NbMode.Nb2 }); }
                break;
            }
            case ZeusMidiCommand.AutoNotchOnOff:
            {
                if (value > 0) { var cur = st.Snapshot().Nr ?? new NrConfig(); r.SetNr(cur with { AnfEnabled = !cur.AnfEnabled }); }
                break;
            }
            case ZeusMidiCommand.SpectralNbOnOff:
            {
                if (value > 0) { var cur = st.Snapshot().Nr ?? new NrConfig(); r.SetNr(cur with { SnbEnabled = !cur.SnbEnabled }); }
                break;
            }
            case ZeusMidiCommand.SquelchOnOff:
            {
                if (value > 0) { var cur = st.Snapshot().Squelch ?? new SquelchConfig(); r.SetSquelch(cur with { Enabled = !cur.Enabled }); }
                break;
            }
            case ZeusMidiCommand.SquelchLevel:
            {
                var cur = st.Snapshot().Squelch ?? new SquelchConfig(); r.SetSquelch(cur with { Level = Math.Clamp((int)Math.Round(st.Scale(value, 0, 100)), 0, 100) });
                break;
            }
            case ZeusMidiCommand.Rx1AutoAgc:
            {
                if (value > 0) { r.SetAutoAgc(!st.Snapshot().AutoAgcEnabled); }
                break;
            }
            case ZeusMidiCommand.SetAfGain:
            {
                r.SetRxAfGain(st.Scale(value, -50.0, 20.0));
                break;
            }
            case ZeusMidiCommand.VolumeVfoA:
            {
                r.SetRxAfGain(st.Scale(value, -50.0, 20.0));
                break;
            }
            case ZeusMidiCommand.VolumeVfoB:
            {
                r.SetRx2(new Rx2SetRequest(AfGainDb: st.Scale(value, -50.0, 20.0)));
                break;
            }
            case ZeusMidiCommand.VolumeVfoAIncr:
            {
                r.SetRxAfGain(st.Snapshot().RxAfGainDb + delta);
                break;
            }
            case ZeusMidiCommand.VolumeVfoBIncr:
            {
                r.SetRx2(new Rx2SetRequest(AfGainDb: st.Snapshot().Rx2().AfGainDb + delta));
                break;
            }
            case ZeusMidiCommand.Rx2Volume:
            {
                r.SetRx2(new Rx2SetRequest(AfGainDb: st.Scale(value, -50.0, 20.0)));
                break;
            }
            case ZeusMidiCommand.MuteOnOff:
            {
                if (value > 0) r.SetReceiverMuted(0, !st.Snapshot().Rx1Muted);
                break;
            }
            case ZeusMidiCommand.Rx2MuteOnOff:
            {
                if (value > 0) r.SetReceiverMuted(1, !st.Snapshot().Rx2Muted);
                break;
            }
            case ZeusMidiCommand.MonOnOff:
            {
                if (value > 0) r.SetTxMonitor(new TxMonitorSetRequest(!st.Snapshot().TxMonitorEnabled));
                break;
            }
            case ZeusMidiCommand.MicGain:
            {
                r.SetTxMicGain((int)Math.Round(st.Scale(value, -40.0, 10.0)));
                break;
            }
            case ZeusMidiCommand.PreAmpSettingsKnob:
            {
                r.SetAttenuator(new HpsdrAtten((int)Math.Round(st.Scale(value, HpsdrAtten.MinDb, HpsdrAtten.MaxDb))));
                break;
            }
            case ZeusMidiCommand.MoxOnOff:
            {
                // TX keying only ever fires on a discrete button press (value > 0).
                // A continuous knob/wheel binding must never key the transmitter;
                // the live MoxOn state is the toggle source so a non-MIDI unkey
                // can't desync this.
                if (value <= 0) break;
                if (!tx.TrySetMox(!tx.IsMoxOn, MoxSource.Midi, out var err))
                    _log.LogDebug("midi.mox failed: {Err}", err);
                break;
            }
            case ZeusMidiCommand.TunOnOff:
            {
                if (value <= 0) break;
                if (!tx.TrySetTun(!tx.IsTunOn, out var err))
                    _log.LogDebug("midi.tun failed: {Err}", err);
                break;
            }
            case ZeusMidiCommand.DriveLevel:
            {
                r.SetDrive((int)System.Math.Round(st.Scale(value, 0, 100)));
                break;
            }
            case ZeusMidiCommand.DriveLevelInc:
            {
                r.SetDrive(System.Math.Clamp(st.Snapshot().DrivePct + delta, 0, 100));
                break;
            }
            case ZeusMidiCommand.TunPowerLevel:
            {
                r.SetTuneDrive((int)System.Math.Round(st.Scale(value, 0, 100)));
                break;
            }
            case ZeusMidiCommand.CompanderOnOff:
            {
                if (value > 0)
                {
                    var lv = st.Snapshot().TxLeveling ?? new Zeus.Contracts.TxLevelingConfig();
                    r.SetTxLeveling(lv with { CompressorEnabled = !lv.CompressorEnabled });
                }
                break;
            }
            case ZeusMidiCommand.TwoToneOnOff:
            {
                if (value <= 0) break;
                if (!tx.TrySetTwoTone(new Zeus.Contracts.TwoToneSetRequest(!tx.IsTwoToneOn), out var err))
                    _log.LogDebug("midi.twotone failed: {Err}", err);
                break;
            }
            case ZeusMidiCommand.SplitOnOff:
            {
                // Split = TX on VFO-B. Derive the next state from the live TxVfo
                // so a UI/CAT split change can't invert the MIDI toggle.
                if (value > 0)
                    r.SetTxVfo(st.Snapshot().TxVfo == Zeus.Contracts.TxVfo.A
                        ? Zeus.Contracts.TxVfo.B : Zeus.Contracts.TxVfo.A);
                break;
            }
            case ZeusMidiCommand.ToggleTx:
            {
                var s = st.Snapshot();
                r.SetTxVfo(s.TxVfo == Zeus.Contracts.TxVfo.A ? Zeus.Contracts.TxVfo.B : Zeus.Contracts.TxVfo.A);
                break;
            }
            case ZeusMidiCommand.CpdrLevel:
            {
                var lv = st.Snapshot().TxLeveling ?? new Zeus.Contracts.TxLevelingConfig();
                r.SetTxLeveling(lv with { CompressorGainDb = st.Scale(value, 0, 20) });
                break;
            }
            case ZeusMidiCommand.ZoomInc:
            {
                if (value > 0) r.SetZoom(Math.Min(st.Snapshot().ZoomLevel + 1, SyntheticDspEngine.MaxZoomLevel));
                break;
            }
            case ZeusMidiCommand.ZoomDec:
            {
                if (value > 0) r.SetZoom(Math.Max(st.Snapshot().ZoomLevel - 1, SyntheticDspEngine.MinZoomLevel));
                break;
            }
            case ZeusMidiCommand.ZoomSliderInc:
            {
                r.SetZoom(Math.Clamp(st.Snapshot().ZoomLevel + delta, SyntheticDspEngine.MinZoomLevel, SyntheticDspEngine.MaxZoomLevel));
                break;
            }
            case ZeusMidiCommand.ZoomSliderFix:
            {
                r.SetZoom((int)Math.Round(st.Scale(value, SyntheticDspEngine.MinZoomLevel, SyntheticDspEngine.MaxZoomLevel)));
                break;
            }
            case ZeusMidiCommand.MultiRxOnOff:
            {
                if (value > 0) r.SetRx2(new Rx2SetRequest(Enabled: !st.Snapshot().Rx2Enabled));
                break;
            }
            case ZeusMidiCommand.RX2OnOff:
            {
                if (value > 0) r.SetRx2(new Rx2SetRequest(Enabled: !st.Snapshot().Rx2Enabled));
                break;
            }
            case ZeusMidiCommand.DiversityEnable:
            {
                if (value > 0) r.SetDiversity(!(st.Snapshot().Diversity?.Enabled ?? false), null, null, null);
                break;
            }
            case ZeusMidiCommand.DiversityPhase:
            {
                r.SetDiversity(null, null, Math.Clamp((st.Snapshot().Diversity?.PhaseDeg ?? 0.0) + delta, -180.0, 180.0), null);
                break;
            }
            case ZeusMidiCommand.DiversityGain:
            {
                r.SetDiversity(null, Math.Clamp((st.Snapshot().Diversity?.Gain ?? 1.0) + delta * 0.01, 0.0, 2.0), null, null);
                break;
            }
            case ZeusMidiCommand.DiversitySource:
            {
                if (value > 0) r.SetDiversity(null, null, null, (st.Snapshot().Diversity?.SourceRx ?? 1) >= 2 ? 1 : 2);
                break;
            }
            // MidiMessagesPerTuneStep* (VFO wheel sensitivity) is not wired to any
            // tune path — the wheel cases apply a fixed Hz-per-detent — so these
            // are catalogued Supported=false and fall through to the parity no-op
            // below rather than mutating an engine-local counter nothing reads.
                default:
                    _log.LogDebug("midi.command.noop cmd={Cmd} value={Value} delta={Delta} (parity-only / unsupported seam)",
                        cmd, value, delta);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "midi.command.dispatch failed cmd={Cmd} value={Value} delta={Delta}", cmd, value, delta);
        }
    }
}

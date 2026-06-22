// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// The G2-Ultra (ANDROMEDA type-5) button and encoder assignments mapped here
// mirror DeskHPSDR's rigctl.c handler (https://github.com/dl1bz/deskhpsdr,
// Heiko DL1BZ) and pihpsdr (https://github.com/dl1ycf/pihpsdr, Christoph
// Wüllen DL1YCF), both GPL-2.0-or-later. See ATTRIBUTIONS.md for provenance.

using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server.FrontPanel;

/// <summary>
/// Translates decoded <see cref="PanelEvent"/>s from a G2-Ultra (type-5)
/// front panel into Zeus radio actions, driving the same
/// <see cref="RadioService"/> / <see cref="TxService"/> seams the web UI uses
/// — so a physical button press and an on-screen click are indistinguishable
/// downstream.
///
/// <para>Panel functions Zeus has no backend for (diversity, ATU, the
/// assignable MULTI knob, long-press menus) are logged at debug as
/// <c>g2panel.unmapped</c> and otherwise ignored — see the gap table in
/// <c>docs/lessons/g2-front-panel.md</c>.</para>
///
/// <para><b>PureSignal:</b> the G2-Ultra panel has no PS push-button (only a
/// status LED), so this router never arms PS. The KB2UKA no-auto-arm
/// invariant is preserved structurally — there is no code path here that
/// touches <c>SetPs</c>.</para>
/// </summary>
public sealed class G2PanelActionRouter
{
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly BandMemoryStore _bandMemory;
    private readonly ILogger _log;

    // Per-panel push-button transition tracking. The panel reports a button
    // value sequence v=0→1→2→0; deskhpsdr keeps a single shared "last v" and
    // derives transitions (tr01 press, tr12 long-hold, tr10 short-release).
    private int _lastV;

    // Tuning granularity for the main VFO knob, in Hz per (accelerated) step.
    // The panel's own acceleration curve is already applied upstream.
    private const long VfoStepHz = 10;
    // Encoder step sizes.
    private const int FilterStepHz = 50;
    private const long RitStepHz = 10;
    private const long RitXitMax = 99_999; // Thetis udRIT/udXIT range
    private const double AfGainStepDb = 1.0;
    private const double AgcStepDb = 1.0;

    // Mode cycle order for MODE+/MODE- (Thetis-like ordering).
    private static readonly RxMode[] ModeOrder =
    {
        RxMode.LSB, RxMode.USB, RxMode.DSB, RxMode.CWL, RxMode.CWU,
        RxMode.FM, RxMode.AM, RxMode.SAM, RxMode.DIGL, RxMode.DIGU,
    };

    // Default band-centre frequencies (Hz) used when band memory is empty.
    private static readonly Dictionary<string, long> BandDefaults = new()
    {
        ["160m"] = 1_840_000,  ["80m"] = 3_700_000,  ["60m"] = 5_357_000,
        ["40m"] = 7_100_000,   ["30m"] = 10_120_000, ["20m"] = 14_100_000,
        ["17m"] = 18_120_000,  ["15m"] = 21_200_000, ["12m"] = 24_950_000,
        ["10m"] = 28_400_000,  ["6m"] = 50_150_000,
    };

    public G2PanelActionRouter(RadioService radio, TxService tx, BandMemoryStore bandMemory, ILogger log)
    {
        _radio = radio;
        _tx = tx;
        _bandMemory = bandMemory;
        _log = log;
    }

    public void Dispatch(PanelEvent ev)
    {
        try
        {
            switch (ev)
            {
                case PanelEvent.Button b: HandleButton(b.Id, b.V); break;
                case PanelEvent.Encoder e: HandleEncoder(e.Id, e.Ticks); break;
                case PanelEvent.Vfo v: HandleVfo(v.Steps); break;
                case PanelEvent.Version: break; // handled by the service
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "g2panel.action.error event={Event}", ev);
        }
    }

    private void HandleVfo(int steps)
    {
        var s = _radio.Snapshot();
        _radio.SetVfo(s.VfoHz + steps * VfoStepHz);
    }

    // ---- Buttons (G2-Ultra / type-5 map) -----------------------------------

    private void HandleButton(int p, int v)
    {
        // Derive transitions from the shared previous value, exactly as the
        // panel firmware expects (short press = tr01; long press = tr12).
        bool tr01 = _lastV == 0 && v == 1;
        bool tr12 = _lastV == 1 && v == 2;
        bool tr10 = _lastV == 1 && v == 0;
        _lastV = v;

        switch (p)
        {
            // Encoder push-buttons -------------------------------------------
            case 1: if (tr01) ToggleMute(1); break;   // MUTE_RX2
            case 2: if (tr01) ToggleMute(0); break;   // MUTE_RX1
            case 3: if (tr01) Unmapped("MULTI_BUTTON (no assignable multi)"); break;

            // Four buttons left of the screen --------------------------------
            case 4: if (tr01) Unmapped("ATU (no ATU control)"); break;
            case 5: if (tr01) ToggleTwoTone(); break;
            case 6: if (tr01) ToggleTune(); break;
            case 7: if (tr01) ToggleMox(); break;

            // Buttons below the VFO knob -------------------------------------
            case 8: if (tr01) ToggleCtun(); break;
            case 9: if (tr01) ToggleLock(); break;    // LOCK

            // Right-hand buttons ---------------------------------------------
            case 10: if (tr01) _radio.SwapVfos(); break;                  // A/B
            case 11: if (tr01) CycleRitXit(); break;                      // RITSELECT
            case 12: if (tr01) ClearRitXit(); break;                      // RIT/XIT clear
            case 13: if (tr01) FilterCutDefault(); break;                 // MULTI 2 push

            // 4x3 pad — "no Band" layer (Mode/Filter/Band stepping) ----------
            case 14: if (tr10) StepMode(+1); if (tr12) Unmapped("MENU_MODE (long)"); break;
            case 15: if (tr10) StepFilter(+1); if (tr12) Unmapped("MENU_FILTER (long)"); break;
            case 16: if (tr01) StepBand(+1); if (tr12) Unmapped("MENU_BAND (long)"); break;
            case 17: if (tr01) StepMode(-1); break;
            case 18: if (tr01) StepFilter(-1); break;
            case 19: if (tr01) StepBand(-1); break;
            case 20: if (tr01) CopyAtoB(); break;                          // A>B
            case 21: if (tr01) CopyBtoA(); break;                          // B>A
            case 22: if (tr01) ToggleSplit(); break;                       // SPLIT
            case 23: if (tr10) ToggleSnb(); if (tr12) Unmapped("MENU_NOISE (long)"); break;
            case 24: if (tr10) ToggleNb(); if (tr12) Unmapped("MENU_NOISE (long)"); break;
            case 25: if (tr10) CycleNr(); if (tr12) Unmapped("MENU_NOISE (long)"); break;

            // 4x3 pad — "Band" layer (direct band select) -------------------
            case 27: if (tr01) SelectBand("160m"); break;
            case 28: if (tr01) SelectBand("80m"); break;
            case 29: if (tr01) SelectBand("60m"); break;
            case 30: if (tr01) SelectBand("40m"); break;
            case 31: if (tr01) SelectBand("30m"); break;
            case 32: if (tr01) SelectBand("20m"); break;
            case 33: if (tr01) SelectBand("17m"); break;
            case 34: if (tr01) SelectBand("15m"); break;
            case 35: if (tr01) SelectBand("12m"); break;
            case 36: if (tr01) SelectBand("10m"); break;
            case 37: if (tr01) SelectBand("6m"); break;
            case 38: if (tr01) Unmapped("BAND_136 / 2200m (outside HF plan)"); break;

            case 41: if (tr01) Unmapped("DIV (diversity backend present; wire on request)"); break;

            default: break; // 26, 39, 40 reserved/unused on the G2-Ultra
        }
    }

    // ---- Encoders (G2-Ultra / type-5 map) ----------------------------------

    private void HandleEncoder(int p, int ticks)
    {
        var s = _radio.Snapshot();
        switch (p)
        {
            case 1: // RX2 AF gain
                _radio.SetRx2(new Rx2SetRequest(
                    AfGainDb: Math.Clamp(s.Rx2AfGainDb + ticks * AfGainStepDb, -50.0, 20.0)));
                break;
            case 2: Unmapped("AGC_GAIN_RX2 (no per-RX AGC top)"); break;
            case 3: // RX1 AF gain
                _radio.SetRxAfGain(Math.Clamp(s.RxAfGainDb + ticks * AfGainStepDb, -50.0, 20.0));
                break;
            case 4: // RX1 AGC top
                _radio.SetAgcTop(s.AgcTopDb + ticks * AgcStepDb);
                break;
            case 5: Unmapped("MULTI_ENC (no assignable multi)"); break;
            case 6: // TX drive
                _radio.SetDrive(Math.Clamp(s.DrivePct + ticks, 0, 100));
                break;
            case 7: AdjustRitXit(s, ticks); break;       // RIT/XIT offset
            case 8: // RX attenuator
                _radio.SetAttenuator(new HpsdrAtten(s.AttenDb + ticks));
                break;
            case 9: // filter cut high
                _radio.SetFilter(s.FilterLowHz, s.FilterHighHz + ticks * FilterStepHz);
                break;
            case 10: // filter cut low
                _radio.SetFilter(s.FilterLowHz + ticks * FilterStepHz, s.FilterHighHz);
                break;
            case 11: Unmapped("DIV_GAIN (diversity backend present; wire on request)"); break;
            case 12: Unmapped("DIV_PHASE (diversity backend present; wire on request)"); break;
            default: break;
        }
    }

    // ---- Action helpers ----------------------------------------------------

    private void ToggleMox()
    {
        // Panel MOX behaves exactly like the on-screen MOX button (master
        // source, full pre-key amp-relay protection).
        if (!_tx.TrySetMox(!_tx.IsMoxOn, MoxSource.UI, out var err) && err is not null)
            _log.LogInformation("g2panel.mox.refused {Err}", err);
    }

    private void ToggleTune()
    {
        if (!_tx.TrySetTun(!_tx.IsTunOn, out var err) && err is not null)
            _log.LogInformation("g2panel.tune.refused {Err}", err);
    }

    private void ToggleTwoTone()
    {
        var s = _radio.Snapshot();
        if (!_tx.TrySetTwoTone(new TwoToneSetRequest(!s.TwoToneEnabled, 700, 1900, 0.5), out var err)
            && err is not null)
            _log.LogInformation("g2panel.twotone.refused {Err}", err);
    }

    private void ToggleCtun() => _radio.SetCtunEnabled(!_radio.Snapshot().CtunEnabled);

    // VFO lock (Thetis chkVFOLock): blocks operator dial tuning. The panel's
    // own VFO knob is an operator input, so a locked VFO also ignores it — the
    // RadioService.SetVfo guard enforces that for us.
    private void ToggleLock() => _radio.SetVfoLock(!_radio.Snapshot().VfoLocked);

    // Per-RX audio mute. index 0 = RX1, 1 = RX2.
    private void ToggleMute(int index)
    {
        var s = _radio.Snapshot();
        bool cur = index == 0 ? s.Rx1Muted : s.Rx2Muted;
        _radio.SetReceiverMuted(index, !cur);
    }

    // RITSELECT cycles none → RIT → XIT → none, matching the original
    // ANDROMEDA RIT/XIT button (deskhpsdr type-1 case 42).
    private void CycleRitXit()
    {
        var s = _radio.Snapshot();
        if (!s.RitEnabled && !s.XitEnabled)
        {
            _radio.SetRit(enabled: true, hz: null);
        }
        else if (s.RitEnabled && !s.XitEnabled)
        {
            _radio.SetRit(enabled: false, hz: null);
            _radio.SetXit(enabled: true, hz: null);
        }
        else
        {
            _radio.SetRit(enabled: false, hz: null);
            _radio.SetXit(enabled: false, hz: null);
        }
    }

    // Clear zeroes both offsets (keeps enable state), like RIT_CLEAR/XIT_CLEAR.
    private void ClearRitXit()
    {
        _radio.SetRit(enabled: null, hz: 0);
        _radio.SetXit(enabled: null, hz: 0);
    }

    // The "RIT/ATTN" encoder nudges whichever offset is active. RIT wins when
    // both are on; if neither is enabled it adjusts (and implicitly arms) RIT.
    private void AdjustRitXit(StateDto s, int ticks)
    {
        long delta = ticks * RitStepHz;
        if (s.XitEnabled && !s.RitEnabled)
            _radio.SetXit(enabled: null, hz: Math.Clamp(s.XitHz + delta, -RitXitMax, RitXitMax));
        else
            _radio.SetRit(enabled: s.RitEnabled ? (bool?)null : true,
                          hz: Math.Clamp(s.RitHz + delta, -RitXitMax, RitXitMax));
    }

    private void ToggleSplit()
    {
        // Zeus has no SPLIT flag; the nearest equivalent is flipping which VFO
        // feeds TX. Toggling A<->B gives the operator cross-VFO transmit.
        var s = _radio.Snapshot();
        _radio.SetTxVfo(s.TxVfo == TxVfo.A ? TxVfo.B : TxVfo.A);
    }

    private void CopyAtoB() { var s = _radio.Snapshot(); _radio.SetVfoB(s.VfoHz); }
    private void CopyBtoA() { var s = _radio.Snapshot(); _radio.SetVfo(s.VfoBHz); }

    private void StepMode(int dir)
    {
        var cur = _radio.Snapshot().Mode;
        int i = Array.IndexOf(ModeOrder, cur);
        if (i < 0) i = 0;
        int next = ((i + dir) % ModeOrder.Length + ModeOrder.Length) % ModeOrder.Length;
        _radio.SetMode(ModeOrder[next]);
    }

    private void StepFilter(int dir)
    {
        // No preset table in the backend: approximate FILTER+/- as widen /
        // narrow around the passband centre. Documented as approximate.
        var s = _radio.Snapshot();
        int low = s.FilterLowHz, high = s.FilterHighHz;
        int width = high - low;
        if (width <= 0) return;
        int delta = Math.Max(FilterStepHz, width / 10) * dir; // dir>0 = wider
        int newWidth = Math.Clamp(width + delta, 50, 20_000);
        int center = (low + high) / 2;
        _radio.SetFilter(center - newWidth / 2, center + newWidth / 2);
    }

    private void FilterCutDefault()
    {
        // "MULTI 2" push = filter-cut defaults. RadioService re-applies the
        // mode's default passband when SetMode is invoked with the current
        // mode, which is the cheapest way to restore defaults here.
        var s = _radio.Snapshot();
        _radio.SetMode(s.Mode);
    }

    private void StepBand(int dir)
    {
        var s = _radio.Snapshot();
        var cur = BandUtils.FreqToBand(s.VfoHz);
        var bands = BandUtils.HfBands;
        int i = cur is null ? 0 : Math.Max(0, bands.ToList().IndexOf(cur));
        int next = ((i + dir) % bands.Count + bands.Count) % bands.Count;
        SelectBand(bands[next]);
    }

    private void SelectBand(string band)
    {
        var mem = _bandMemory.Get(band);
        if (mem is not null)
        {
            _radio.SetVfo(mem.Hz);
            _radio.SetMode(mem.Mode);
        }
        else if (BandDefaults.TryGetValue(band, out long hz))
        {
            _radio.SetVfo(hz);
        }
        else
        {
            Unmapped($"band {band} (no memory or default)");
        }
    }

    private void ToggleNb()
    {
        var nr = _radio.Snapshot().Nr ?? new NrConfig();
        _radio.SetNr(nr with { NbMode = nr.NbMode == NbMode.Off ? NbMode.Nb1 : NbMode.Off });
    }

    private void ToggleSnb()
    {
        var nr = _radio.Snapshot().Nr ?? new NrConfig();
        _radio.SetNr(nr with { SnbEnabled = !nr.SnbEnabled });
    }

    private void CycleNr()
    {
        var nr = _radio.Snapshot().Nr ?? new NrConfig();
        NrMode next = nr.NrMode switch
        {
            NrMode.Off => NrMode.Anr,
            NrMode.Anr => NrMode.Emnr,
            NrMode.Emnr => NrMode.Sbnr,
            _ => NrMode.Off,
        };
        _radio.SetNr(nr with { NrMode = next });
    }

    private void Unmapped(string what) => _log.LogDebug("g2panel.unmapped {What}", what);
}

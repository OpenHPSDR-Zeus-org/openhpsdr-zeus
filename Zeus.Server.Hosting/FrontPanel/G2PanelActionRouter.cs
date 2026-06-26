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
// The G2-Ultra button / encoder / LED assignments mapped here follow the
// authoritative Thetis G2 panel dataset (MW0LGE/G8NJJ Andromeda.cs
// MakeNewG2PanelDataset) and DeskHPSDR's rigctl.c type-5 handler
// (https://github.com/dl1bz/deskhpsdr, Heiko DL1BZ) — both GPL-2.0-or-later.
// See ATTRIBUTIONS.md for the full provenance statement.

using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server.FrontPanel;

/// <summary>
/// Translates decoded <see cref="PanelEvent"/>s from a G2-Ultra front panel
/// into Zeus radio actions, driving the same <see cref="RadioService"/> /
/// <see cref="TxService"/> seams the web UI uses — so a physical button press
/// and an on-screen click are indistinguishable downstream.
///
/// <para>The default assignments mirror Thetis's G2 panel dataset. Where
/// Thetis would pop an on-panel softkey menu (long-press of MODE/FILTER/BAND/
/// NOISE), Zeus has no panel screen — the web UI is the menu — so those
/// long-press actions are intentionally no-ops. A couple of panel functions
/// have no Zeus backend yet (per-RX AGC top on the RX2 encoder); they log
/// <c>g2panel.unmapped</c>. See <c>docs/lessons/g2-front-panel.md</c>.</para>
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
    private readonly ToolbarSettingsStore _toolbarSettings;
    private readonly ILogger _log;

    // Per-panel push-button transition tracking. The panel reports a button
    // value sequence v=0→1→2→0; deskhpsdr keeps a single shared "last v" and
    // derives transitions (tr01 press, tr12 long-hold, tr10 short-release).
    private int _lastV;

    // Multifunction-encoder selection (Thetis eENMulti). The MULTI push-button
    // cycles which parameter the MULTI encoder adjusts.
    private int _multiIndex;

    // Net VFO-knob movement accumulated since the last service flush. The serial
    // read path only adds to this counter; RadioService retunes happen off that
    // path so a fast spin cannot backlog serial reads behind state broadcasts.
    private long _pendingVfoSteps;

    // Encoder step sizes.
    private const int FilterStepHz = 50;
    private const long RitStepHz = 10;
    private const long RitXitMax = 99_999; // Thetis udRIT/udXIT range
    private const double AfGainStepDb = 1.0;
    private const double AgcStepDb = 1.0;
    private const double DivGainStep = 0.05;   // 0..2 magnitude
    private const double DivPhaseStepDeg = 1.0; // -180..180
    private const int AtuTunePulseMs = 250;     // ATU tune-request pulse width

    // Mode cycle order for MODE+/MODE- (Thetis-like ordering).
    private static readonly RxMode[] ModeOrder =
    {
        RxMode.LSB, RxMode.USB, RxMode.DSB, RxMode.CWL, RxMode.CWU,
        RxMode.FM, RxMode.AM, RxMode.SAM, RxMode.DIGL, RxMode.DIGU,
    };

    // Default band-centre frequencies (Hz) used when band memory is empty.
    // LF/MF (630 m, 2200 m) back the BandLFMF button (Thetis eBBBandLFMF).
    private static readonly Dictionary<string, long> BandDefaults = new()
    {
        ["2200m"] = 137_500,   ["630m"] = 475_500,
        ["160m"] = 1_840_000,  ["80m"] = 3_700_000,  ["60m"] = 5_357_000,
        ["40m"] = 7_100_000,   ["30m"] = 10_120_000, ["20m"] = 14_100_000,
        ["17m"] = 18_120_000,  ["15m"] = 21_200_000, ["12m"] = 24_950_000,
        ["10m"] = 28_400_000,  ["6m"] = 50_150_000,
    };

    // Assignable MULTI-encoder functions (Thetis MultiTable ∩ Zeus backend).
    // Index cycled by the MULTI push-button; the MULTI encoder calls Apply.
    private readonly (string Name, Action<int> Apply)[] _multi;

    public G2PanelActionRouter(
        RadioService radio,
        TxService tx,
        BandMemoryStore bandMemory,
        ToolbarSettingsStore toolbarSettings,
        ILogger log)
    {
        _radio = radio;
        _tx = tx;
        _bandMemory = bandMemory;
        _toolbarSettings = toolbarSettings;
        _log = log;

        _multi = new (string, Action<int>)[]
        {
            ("RX1 AF Gain",     AdjustAfRx1),
            ("RX2 AF Gain",     AdjustAfRx2),
            ("RX1 AGC",         AdjustAgcRx1),
            ("RX1 Step Atten",  AdjustAtten),
            ("Filter High Cut", AdjustFilterHigh),
            ("Filter Low Cut",  AdjustFilterLow),
            ("RIT",             AdjustRit),
            ("XIT",             AdjustXit),
            ("TX Drive",        AdjustDrive),
            ("Diversity Gain",  AdjustDivGain),
            ("Diversity Phase", AdjustDivPhase),
        };
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
        Interlocked.Add(ref _pendingVfoSteps, steps);
    }

    public void FlushPendingVfo()
    {
        long steps = Interlocked.Exchange(ref _pendingVfoSteps, 0);
        if (steps == 0) return;

        try
        {
            var s = _radio.Snapshot();
            int stepHz = _toolbarSettings.CurrentStepHz;
            _radio.SetVfo(ApplyVfoSteps(s.VfoHz, steps, stepHz));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "g2panel.vfo.flush.error steps={Steps}", steps);
        }
    }

    internal static long ApplyVfoSteps(long currentHz, long steps, int stepHz)
    {
        int step = ToolbarSettingsStore.NormalizeStepHz(stepHz);
        long target = currentHz + steps * step;
        return RoundToStep(target, step);
    }

    private static long RoundToStep(long hz, int stepHz)
    {
        if (hz <= 0) return 0;
        if (stepHz <= 1) return hz;

        long quotient = Math.DivRem(hz, stepHz, out long remainder);
        if (remainder * 2 >= stepHz) quotient++;
        return quotient > long.MaxValue / stepHz ? long.MaxValue : quotient * stepHz;
    }

    // ---- Buttons (G2-Ultra map; Thetis MakeNewG2PanelDataset) ---------------

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
            case 3: if (tr01) CycleMulti(); break;    // MULTI encoder button

            // Four buttons left of the screen --------------------------------
            case 4: if (tr01) _radio.RequestAtuTune(AtuTunePulseMs); break;  // ATU
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
            case 13: if (tr01) FilterCutDefault(); break;                 // filter reset

            // 4x3 pad — "no Band" layer (Mode/Filter/Band stepping) ----------
            // Long-press (tr12) would open an on-panel menu in Thetis; Zeus
            // uses its web UI for menus, so the long action is a no-op.
            case 14: if (tr10) StepMode(+1); break;
            case 15: if (tr10) StepFilter(+1); break;
            case 16: if (tr01) StepBand(+1); break;
            case 17: if (tr01) StepMode(-1); break;
            case 18: if (tr01) StepFilter(-1); break;
            case 19: if (tr01) StepBand(-1); break;
            case 20: if (tr01) CopyAtoB(); break;                          // A>B
            case 21: if (tr01) CopyBtoA(); break;                          // B>A
            case 22: if (tr01) ToggleSplit(); break;                       // SPLIT
            case 23: if (tr10) ToggleSnb(); break;                         // F1
            case 24: if (tr10) ToggleNb(); break;                          // F2
            case 25: if (tr10) CycleNr(); break;                           // F3

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
            case 38: if (tr01) SelectLfMf(); break;                        // LF/MF

            case 41: if (tr01) ToggleDiversity(); break;                   // DIV

            default: break; // 26, 39, 40, 42-50 reserved/unused on the G2-Ultra
        }
    }

    // ---- Encoders (G2-Ultra map; Thetis MakeNewG2PanelDataset) --------------

    private void HandleEncoder(int p, int ticks)
    {
        switch (p)
        {
            case 1: AdjustAfRx2(ticks); break;        // RX2 AF gain
            case 2: Unmapped("AGC_GAIN_RX2 (no per-RX AGC top)"); break;
            case 3: AdjustAfRx1(ticks); break;        // RX1 AF gain
            case 4: AdjustAgcRx1(ticks); break;       // RX1 AGC top
            case 5: ApplyMulti(ticks); break;         // MULTI encoder
            case 6: AdjustDrive(ticks); break;        // TX drive
            case 7: AdjustRitXit(ticks); break;       // RIT/XIT offset (silk RIT/ATTN inner)
            case 8: AdjustAtten(ticks); break;        // RX attenuator (silk RIT/ATTN outer)
            case 9: AdjustFilterHigh(ticks); break;   // filter cut high
            case 10: AdjustFilterLow(ticks); break;   // filter cut low
            case 11: AdjustDivGain(ticks); break;     // diversity gain
            case 12: AdjustDivPhase(ticks); break;    // diversity phase
            default: break;
        }
    }

    // ---- Encoder adjustment helpers (shared with MULTI) --------------------

    private void AdjustAfRx1(int ticks)
    {
        var s = _radio.Snapshot();
        _radio.SetRxAfGain(Math.Clamp(s.RxAfGainDb + ticks * AfGainStepDb, -50.0, 20.0));
    }

    private void AdjustAfRx2(int ticks)
    {
        var s = _radio.Snapshot();
        _radio.SetRx2(new Rx2SetRequest(
            AfGainDb: Math.Clamp(s.Rx2().AfGainDb + ticks * AfGainStepDb, -50.0, 20.0)));
    }

    private void AdjustAgcRx1(int ticks)
    {
        var s = _radio.Snapshot();
        _radio.SetAgcTop(s.AgcTopDb + ticks * AgcStepDb);
    }

    private void AdjustDrive(int ticks)
    {
        var s = _radio.Snapshot();
        _radio.SetDrive(Math.Clamp(s.DrivePct + ticks, 0, 100));
    }

    private void AdjustAtten(int ticks)
    {
        var s = _radio.Snapshot();
        _radio.SetAttenuator(new HpsdrAtten(s.AttenDb + ticks));
    }

    private void AdjustFilterHigh(int ticks)
    {
        var s = _radio.Snapshot();
        _radio.SetFilter(s.FilterLowHz, s.FilterHighHz + ticks * FilterStepHz);
    }

    private void AdjustFilterLow(int ticks)
    {
        var s = _radio.Snapshot();
        _radio.SetFilter(s.FilterLowHz + ticks * FilterStepHz, s.FilterHighHz);
    }

    private void AdjustRit(int ticks)
    {
        var s = _radio.Snapshot();
        _radio.SetRit(enabled: s.RitEnabled ? (bool?)null : true,
                      hz: Math.Clamp(s.RitHz + ticks * RitStepHz, -RitXitMax, RitXitMax));
    }

    private void AdjustXit(int ticks)
    {
        var s = _radio.Snapshot();
        _radio.SetXit(enabled: s.XitEnabled ? (bool?)null : true,
                      hz: Math.Clamp(s.XitHz + ticks * RitStepHz, -RitXitMax, RitXitMax));
    }

    // The "RIT/ATTN" encoder nudges whichever offset is active. RIT wins when
    // both are on; if neither is enabled it adjusts (and implicitly arms) RIT.
    private void AdjustRitXit(int ticks)
    {
        var s = _radio.Snapshot();
        if (s.XitEnabled && !s.RitEnabled) AdjustXit(ticks);
        else AdjustRit(ticks);
    }

    private void AdjustDivGain(int ticks)
    {
        var cur = _radio.Snapshot().Diversity ?? new DiversityConfig();
        _radio.SetDiversity(enabled: null,
            gain: Math.Clamp(cur.Gain + ticks * DivGainStep, 0.0, 2.0),
            phaseDeg: null, sourceRx: null);
    }

    private void AdjustDivPhase(int ticks)
    {
        var cur = _radio.Snapshot().Diversity ?? new DiversityConfig();
        _radio.SetDiversity(enabled: null, gain: null,
            phaseDeg: Math.Clamp(cur.PhaseDeg + ticks * DivPhaseStepDeg, -180.0, 180.0),
            sourceRx: null);
    }

    // ---- MULTI encoder -----------------------------------------------------

    private void CycleMulti()
    {
        _multiIndex = (_multiIndex + 1) % _multi.Length;
        _log.LogInformation("g2panel.multi.select {Name}", _multi[_multiIndex].Name);
    }

    private void ApplyMulti(int ticks) => _multi[_multiIndex].Apply(ticks);

    // ---- Button action helpers ---------------------------------------------

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

    private void ToggleDiversity()
    {
        var cur = _radio.Snapshot().Diversity ?? new DiversityConfig();
        _radio.SetDiversity(enabled: !cur.Enabled, gain: null, phaseDeg: null, sourceRx: null);
    }

    // RITSELECT cycles none → RIT → XIT → none, matching the original
    // ANDROMEDA RIT/XIT button (Thetis eBBRITXITToggle).
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

    // Clear zeroes both offsets (keeps enable state), like eBBClearRITXIT.
    private void ClearRitXit()
    {
        _radio.SetRit(enabled: null, hz: 0);
        _radio.SetXit(enabled: null, hz: 0);
    }

    private void ToggleSplit()
    {
        // Zeus has no SPLIT flag; the nearest equivalent is flipping which VFO
        // feeds TX. Toggling A<->B gives the operator cross-VFO transmit.
        var s = _radio.Snapshot();
        _radio.SetTxVfo(s.TxVfo == TxVfo.A ? TxVfo.B : TxVfo.A);
    }

    private void CopyAtoB() { var s = _radio.Snapshot(); _radio.SetVfoB(s.VfoHz); }
    private void CopyBtoA() { var s = _radio.Snapshot(); _radio.SetVfo(s.Rx2().VfoHz); }

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
        // Filter reset: RadioService re-applies the mode's default passband when
        // SetMode is invoked with the current mode — cheapest way to reset here.
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

    // BandLFMF: toggle between 2200 m and 630 m on repeated presses.
    private void SelectLfMf()
    {
        long hz = _radio.Snapshot().VfoHz;
        SelectBand(hz < 300_000 ? "630m" : "2200m");
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

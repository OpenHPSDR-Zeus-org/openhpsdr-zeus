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
    private enum ButtonAction
    {
        ToggleMuteRx2,
        ToggleMuteRx1,
        CycleMulti,
        AtuTune,
        ToggleTwoTone,
        ToggleTune,
        ToggleMox,
        ToggleCtun,
        ToggleLock,
        SwapVfos,
        CycleRitXit,
        ClearRitXit,
        FilterCutDefault,
        ModePlus,
        FilterPlus,
        BandPlus,
        ModeMinus,
        FilterMinus,
        BandMinus,
        CopyAtoB,
        CopyBtoA,
        ToggleSplit,
        ToggleSnb,
        ToggleNb,
        CycleNr,
        Band160,
        Band80,
        Band60,
        Band40,
        Band30,
        Band20,
        Band17,
        Band15,
        Band12,
        Band10,
        Band6,
        BandLfMf,
        ToggleDiversity,
    }

    private enum EncoderAction
    {
        AfRx2,
        AgcRx2,
        AfRx1,
        AgcRx1,
        Multi,
        Drive,
        RitXit,
        Atten,
        FilterHigh,
        FilterLow,
        DivGain,
        DivPhase,
        Rit,
        Xit,
        Count,
    }

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

    // Net VFO-knob movement accumulated since the last service flush. These are
    // accelerated encoder ticks from the panel, not final Hz-step detents. The
    // serial read path only adds to this counter; RadioService retunes happen
    // off that path so a fast spin cannot backlog serial reads behind state
    // broadcasts.
    private long _pendingVfoTicks;

    // Non-VFO encoder movement, accumulated by logical action. DeskHPSDR queues
    // ANDROMEDA actions onto the GTK idle loop; this gives Zeus the same shape:
    // serial reads only enqueue ticks, while radio-state mutations happen on a
    // steady panel timer.
    private readonly long[] _pendingEncoderTicks = new long[(int)EncoderAction.Count];

    // DeskHPSDR's action layer keeps a VFO accumulator and divides by
    // vfo_encoder_divisor before calling vfo_step(). Zeus computes the divisor
    // from the selected toolbar step so coarse steps stay controllable while
    // 1 Hz / 10 Hz steps remain responsive.
    private long _vfoTickAccumulator;

    // Encoder step sizes.
    private const int FilterStepHz = 50;
    private const int VfoEncoderReferenceStepHz = 10;
    private const int MinVfoEncoderDivisor = 1;
    private const int MaxVfoEncoderDivisor = 60;
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
    private readonly (string Name, EncoderAction Target)[] _multi;

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

        _multi = new (string, EncoderAction)[]
        {
            ("RX1 AF Gain",     EncoderAction.AfRx1),
            ("RX2 AF Gain",     EncoderAction.AfRx2),
            ("RX1 AGC",         EncoderAction.AgcRx1),
            ("RX1 Step Atten",  EncoderAction.Atten),
            ("Filter High Cut", EncoderAction.FilterHigh),
            ("Filter Low Cut",  EncoderAction.FilterLow),
            ("RIT",             EncoderAction.Rit),
            ("XIT",             EncoderAction.Xit),
            ("TX Drive",        EncoderAction.Drive),
            ("Diversity Gain",  EncoderAction.DivGain),
            ("Diversity Phase", EncoderAction.DivPhase),
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
        Interlocked.Add(ref _pendingVfoTicks, steps);
    }

    public bool FlushPendingVfo()
    {
        long ticks = Interlocked.Exchange(ref _pendingVfoTicks, 0);
        if (ticks == 0) return false;

        try
        {
            int stepHz = _toolbarSettings.CurrentStepHz;
            var divided = DivideVfoEncoderTicks(_vfoTickAccumulator, ticks, stepHz);
            _vfoTickAccumulator = divided.RemainderTicks;
            if (divided.LogicalSteps == 0) return false;

            var s = _radio.Snapshot();
            _radio.SetVfo(ApplyVfoSteps(s.VfoHz, divided.LogicalSteps, stepHz));
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "g2panel.vfo.flush.error ticks={Ticks}", ticks);
            return false;
        }
    }

    public bool FlushPendingEncoders()
    {
        bool applied = false;
        for (int i = 0; i < (int)EncoderAction.Count; i++)
        {
            long ticks = Interlocked.Exchange(ref _pendingEncoderTicks[i], 0);
            if (ticks == 0) continue;

            var action = (EncoderAction)i;
            try
            {
                ApplyEncoderAction(action, ClampToInt(ticks));
                applied = true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "g2panel.encoder.flush.error action={Action} ticks={Ticks}", action, ticks);
            }
        }
        return applied;
    }

    public bool FlushPendingPanelWork()
    {
        bool vfoApplied = FlushPendingVfo();
        bool encodersApplied = FlushPendingEncoders();
        return vfoApplied || encodersApplied;
    }

    private static int ClampToInt(long ticks) =>
        ticks > int.MaxValue ? int.MaxValue :
        ticks < int.MinValue ? int.MinValue :
        (int)ticks;

    internal static int VfoEncoderDivisorForStep(int stepHz)
    {
        int step = ToolbarSettingsStore.NormalizeStepHz(stepHz);
        int divisor = (step + VfoEncoderReferenceStepHz - 1) / VfoEncoderReferenceStepHz;
        return Math.Clamp(divisor, MinVfoEncoderDivisor, MaxVfoEncoderDivisor);
    }

    internal static (long LogicalSteps, long RemainderTicks) DivideVfoEncoderTicks(
        long accumulatedTicks,
        long incomingTicks,
        int stepHz)
    {
        int divisor = VfoEncoderDivisorForStep(stepHz);
        long total = accumulatedTicks + incomingTicks;
        long logicalSteps = total / divisor;
        return (logicalSteps, total - logicalSteps * divisor);
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
        var action = ButtonActionForTransition(p, _lastV, v);
        _lastV = v;

        if (action is not null)
        {
            FlushPendingPanelWork();
            ApplyButtonAction(action.Value);
        }
    }

    private static ButtonAction? ButtonActionForTransition(int p, int previousV, int v)
    {
        bool tr01 = previousV == 0 && v == 1;
        bool tr10 = previousV == 1 && v == 0;

        return p switch
        {
            // Encoder push-buttons -------------------------------------------
            1 when tr01 => ButtonAction.ToggleMuteRx2,
            2 when tr01 => ButtonAction.ToggleMuteRx1,
            3 when tr01 => ButtonAction.CycleMulti,

            // Four buttons left of the screen --------------------------------
            4 when tr01 => ButtonAction.AtuTune,
            5 when tr01 => ButtonAction.ToggleTwoTone,
            6 when tr01 => ButtonAction.ToggleTune,
            7 when tr01 => ButtonAction.ToggleMox,

            // Buttons below the VFO knob -------------------------------------
            8 when tr01 => ButtonAction.ToggleCtun,
            9 when tr01 => ButtonAction.ToggleLock,

            // Right-hand buttons ---------------------------------------------
            10 when tr01 => ButtonAction.SwapVfos,
            11 when tr01 => ButtonAction.CycleRitXit,
            12 when tr01 => ButtonAction.ClearRitXit,
            13 when tr01 => ButtonAction.FilterCutDefault,

            // 4x3 pad — "no Band" layer (Mode/Filter/Band stepping) ----------
            // Long-press (tr12) would open an on-panel menu in Thetis; Zeus
            // uses its web UI for menus, so the long action is a no-op.
            14 when tr10 => ButtonAction.ModePlus,
            15 when tr10 => ButtonAction.FilterPlus,
            16 when tr01 => ButtonAction.BandPlus,
            17 when tr01 => ButtonAction.ModeMinus,
            18 when tr01 => ButtonAction.FilterMinus,
            19 when tr01 => ButtonAction.BandMinus,
            20 when tr01 => ButtonAction.CopyAtoB,
            21 when tr01 => ButtonAction.CopyBtoA,
            22 when tr01 => ButtonAction.ToggleSplit,
            23 when tr10 => ButtonAction.ToggleSnb,
            24 when tr10 => ButtonAction.ToggleNb,
            25 when tr10 => ButtonAction.CycleNr,

            // 4x3 pad — "Band" layer (direct band select) -------------------
            27 when tr01 => ButtonAction.Band160,
            28 when tr01 => ButtonAction.Band80,
            29 when tr01 => ButtonAction.Band60,
            30 when tr01 => ButtonAction.Band40,
            31 when tr01 => ButtonAction.Band30,
            32 when tr01 => ButtonAction.Band20,
            33 when tr01 => ButtonAction.Band17,
            34 when tr01 => ButtonAction.Band15,
            35 when tr01 => ButtonAction.Band12,
            36 when tr01 => ButtonAction.Band10,
            37 when tr01 => ButtonAction.Band6,
            38 when tr01 => ButtonAction.BandLfMf,

            41 when tr01 => ButtonAction.ToggleDiversity,

            _ => null, // 26, 39, 40, 42-50 reserved/unused on the G2-Ultra
        };
    }

    internal static string? G2UltraButtonActionNameForTransition(int p, int previousV, int v) =>
        ButtonActionForTransition(p, previousV, v)?.ToString();

    private void ApplyButtonAction(ButtonAction action)
    {
        switch (action)
        {
            case ButtonAction.ToggleMuteRx2: ToggleMute(1); break;
            case ButtonAction.ToggleMuteRx1: ToggleMute(0); break;
            case ButtonAction.CycleMulti: CycleMulti(); break;
            case ButtonAction.AtuTune: _radio.RequestAtuTune(AtuTunePulseMs); break;
            case ButtonAction.ToggleTwoTone: ToggleTwoTone(); break;
            case ButtonAction.ToggleTune: ToggleTune(); break;
            case ButtonAction.ToggleMox: ToggleMox(); break;
            case ButtonAction.ToggleCtun: ToggleCtun(); break;
            case ButtonAction.ToggleLock: ToggleLock(); break;
            case ButtonAction.SwapVfos: _radio.SwapVfos(); break;
            case ButtonAction.CycleRitXit: CycleRitXit(); break;
            case ButtonAction.ClearRitXit: ClearRitXit(); break;
            case ButtonAction.FilterCutDefault: FilterCutDefault(); break;
            case ButtonAction.ModePlus: StepMode(+1); break;
            case ButtonAction.FilterPlus: StepFilter(+1); break;
            case ButtonAction.BandPlus: StepBand(+1); break;
            case ButtonAction.ModeMinus: StepMode(-1); break;
            case ButtonAction.FilterMinus: StepFilter(-1); break;
            case ButtonAction.BandMinus: StepBand(-1); break;
            case ButtonAction.CopyAtoB: CopyAtoB(); break;
            case ButtonAction.CopyBtoA: CopyBtoA(); break;
            case ButtonAction.ToggleSplit: ToggleSplit(); break;
            case ButtonAction.ToggleSnb: ToggleSnb(); break;
            case ButtonAction.ToggleNb: ToggleNb(); break;
            case ButtonAction.CycleNr: CycleNr(); break;
            case ButtonAction.Band160: SelectBand("160m"); break;
            case ButtonAction.Band80: SelectBand("80m"); break;
            case ButtonAction.Band60: SelectBand("60m"); break;
            case ButtonAction.Band40: SelectBand("40m"); break;
            case ButtonAction.Band30: SelectBand("30m"); break;
            case ButtonAction.Band20: SelectBand("20m"); break;
            case ButtonAction.Band17: SelectBand("17m"); break;
            case ButtonAction.Band15: SelectBand("15m"); break;
            case ButtonAction.Band12: SelectBand("12m"); break;
            case ButtonAction.Band10: SelectBand("10m"); break;
            case ButtonAction.Band6: SelectBand("6m"); break;
            case ButtonAction.BandLfMf: SelectLfMf(); break;
            case ButtonAction.ToggleDiversity: ToggleDiversity(); break;
        }
    }

    // ---- Encoders (G2-Ultra map; Thetis MakeNewG2PanelDataset) --------------

    private void HandleEncoder(int p, int ticks)
    {
        var action = EncoderActionFor(p);
        if (action is null) return;

        if (action == EncoderAction.Multi)
        {
            ApplyMulti(ticks);
        }
        else
        {
            QueueEncoder(action.Value, ticks);
        }
    }

    private static EncoderAction? EncoderActionFor(int p) => p switch
    {
        1 => EncoderAction.AfRx2,
        2 => EncoderAction.AgcRx2,
        3 => EncoderAction.AfRx1,
        4 => EncoderAction.AgcRx1,
        5 => EncoderAction.Multi,
        6 => EncoderAction.Drive,
        7 => EncoderAction.RitXit,
        8 => EncoderAction.Atten,
        9 => EncoderAction.FilterHigh,
        10 => EncoderAction.FilterLow,
        11 => EncoderAction.DivGain,
        12 => EncoderAction.DivPhase,
        _ => null,
    };

    internal static string? G2UltraEncoderActionName(int p) =>
        EncoderActionFor(p)?.ToString();

    private void QueueEncoder(EncoderAction action, int ticks)
    {
        if (ticks == 0) return;
        Interlocked.Add(ref _pendingEncoderTicks[(int)action], ticks);
    }

    private void ApplyEncoderAction(EncoderAction action, int ticks)
    {
        switch (action)
        {
            case EncoderAction.AfRx2: AdjustAfRx2(ticks); break;
            case EncoderAction.AgcRx2: AdjustAgcRx2(ticks); break;
            case EncoderAction.AfRx1: AdjustAfRx1(ticks); break;
            case EncoderAction.AgcRx1: AdjustAgcRx1(ticks); break;
            case EncoderAction.Drive: AdjustDrive(ticks); break;
            case EncoderAction.RitXit: AdjustRitXit(ticks); break;
            case EncoderAction.Atten: AdjustAtten(ticks); break;
            case EncoderAction.FilterHigh: AdjustFilterHigh(ticks); break;
            case EncoderAction.FilterLow: AdjustFilterLow(ticks); break;
            case EncoderAction.DivGain: AdjustDivGain(ticks); break;
            case EncoderAction.DivPhase: AdjustDivPhase(ticks); break;
            case EncoderAction.Rit: AdjustRit(ticks); break;
            case EncoderAction.Xit: AdjustXit(ticks); break;
            case EncoderAction.Multi:
            case EncoderAction.Count:
                break;
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
        AdjustAgcTop(ticks);
    }

    private void AdjustAgcRx2(int ticks)
    {
        // Zeus currently exposes AGC-T as one shared receiver-DSP ceiling.
        // Keep the DeskHPSDR RX2 AGC encoder live by driving that shared slider.
        AdjustAgcTop(ticks);
    }

    private void AdjustAgcTop(int ticks)
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

    private void ApplyMulti(int ticks) => QueueEncoder(_multi[_multiIndex].Target, ticks);

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

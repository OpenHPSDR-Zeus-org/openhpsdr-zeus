# RCA: TX Stage Meters — ALC GR sign inversion + MIC_PK not wired

**Date:** 2026-04-21  
**Branch:** `claude/issue-2-20260421-1826`  
**Issue:** #2

## Symptoms

1. **ALC GR bar never filled** — the gain-reduction bar in the TX Stage Meters panel
   showed 0 dB regardless of input level, even when the ALC was visibly compressing
   the signal.

2. **MIC row missing** — the signal-chain view started at EQ, hiding the post-panel-gain
   mic level that enters WDSP. Thetis shows `MIC → EQ → LVLR → ALC → ALC-GR → OUT`.

## Root causes

### ALC GR sign inversion

`GetTXAMeter(ch, 14)` returns `TXA_ALC_GAIN`. Per `native/wdsp/meter.c:103`:

```c
a->result[a->enum_gain] = 20.0 * mlog10(*a->pgain + 1.0e-40);
```

`pgain` is the linear gain multiplier that the ALC applies. When the ALC is reducing
the signal, `gain < 1.0`, so `20*log10(gain)` is **negative** (e.g., −6 dB for 6 dB of
reduction). The raw WDSP value is therefore ≤ 0 dB when active.

`WdspDspEngine.ProcessTxBlock` stored this raw negative value directly:
```csharp
AlcGr: (float)alcGain,   // bug: -6.0 stored as "gain reduction"
```

The frontend `GrRow` component expects a **positive** gain-reduction dB on a 0..20 dB
scale and clamps with `Math.max(0, ...)`, so a −6 value became 0 — the bar never moved.

**Fix:** negate before storing:
```csharp
AlcGr: (float)-alcGain,  // +6.0 = "6 dB of gain reduction"
```

### MIC_PK not wired

`WdspDspEngine.ProcessTxBlock` already read `TXA_MIC_PK` (index 0) for diagnostic
logging, but did not include it in the `TxStageMeters` snapshot. `TxMetersService`
therefore sent a hard-coded `−100f` placeholder in `TxMetersFrame.MicDbfs`, and
`setMeters` in the frontend intentionally skipped `micDbfs` to avoid clobbering the
browser-worklet mic level.

## Changes

| File | Change |
|------|--------|
| `Zeus.Dsp/TxStageMeters.cs` | Added `MicPk` field; documented `AlcGr` sign convention |
| `Zeus.Dsp/Wdsp/WdspDspEngine.cs` | Store `MicPk` in snapshot; negate `alcGain` → `AlcGr` |
| `Zeus.Server.Hosting/TxMetersService.cs` | Use `stage.MicPk` for `TxMetersFrame.MicDbfs`; removed dead `MicDbfsPlaceholder` |
| `zeus-web/src/state/tx-store.ts` | Added `wdspMicPk` field; `setMeters` maps `m.micDbfs → wdspMicPk` |
| `zeus-web/src/components/TxStageMeters.tsx` | Added MIC row at top of chain |

## Meter chain (post-fix)

| Row    | Store field | Source (WDSP index) | Unit  | Notes |
|--------|-------------|---------------------|-------|-------|
| MIC    | wdspMicPk   | TXA_MIC_PK (0)      | dBFS  | Post-panel-gain, pre-EQ |
| EQ     | eqPk        | TXA_EQ_PK (2)       | dBFS  | |
| LVLR   | lvlrPk      | TXA_LVLR_PK (4)     | dBFS  | Equals EQ when Leveler is off |
| ALC    | alcPk       | TXA_ALC_PK (12)     | dBFS  | Key SSB clipping indicator |
| ALC GR | alcGr       | −TXA_ALC_GAIN (14)  | dB    | Positive; >12 dB = over-driving |
| OUT    | outPk       | TXA_OUT_PK (15)     | dBFS  | Final TX peak before EP2 packer |

Note: `micDbfs` in `TxState` remains worklet-driven (pre-WDSP, animates during RX).
`wdspMicPk` is server-driven (post-panel-gain, −∞ when MOX off).

## What was NOT changed

- `Zeus.Contracts/TxMetersFrame.cs` — wire format unchanged (red-light per CLAUDE.md).
  `MicDbfs` field now carries real WDSP data instead of the placeholder.
- `Zeus.Protocol1` and `Zeus.Dsp` initialization order — no changes.
- No new NuGet or npm dependencies.

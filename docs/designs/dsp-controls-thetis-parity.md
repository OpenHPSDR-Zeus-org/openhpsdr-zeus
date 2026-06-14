# DSP Controls — Thetis Parity (AGC, RX Squelch, TX Leveling, Bandwidth)

**Status:** Approved to implement — maintainer directive: mirror Thetis values verbatim
("Thetis is battle-tested; if it's in there it's good to go"). R1/R5/Auto-AGC all resolved to
Thetis parity. Remaining gates are integration-verification only (high-rate DDC), not design.
**Author:** AI agent (plan-first; no code written yet)
**Target radio:** ANAN G2 (OrionMkII / 0x0A family), Protocol 2; pattern is board-agnostic
**Reference:** Thetis (`C:\Users\Admin\Desktop\Thetis-master`) — sole authority for behavior/defaults

---

## 1. Goal & Scope

Bring Zeus's RX/TX DSP control surface up to Thetis parity for the controls an operator
actually reaches for, and build the settings UI to drive them. Every value, range, and
default below is extracted from Thetis source so the port mirrors the reference rather than
inventing behavior.

**In scope (this proposal):**
1. **Full AGC** — mode selector (Fixed/Long/Slow/Med/Fast/Custom) + custom hang/decay/slope/threshold
2. **RX Squelch** — voice (SSQL), AM (AMSQ), FM (FMSQ), auto-selected by mode like Thetis
3. **TX Leveling** — ALC (max gain + decay), Leveler (top + decay), Compressor/CPDR (run + gain)
4. **Bandwidth / sample rate** — confirm/extend RX sample-rate options and per-mode filter-width limits to ANAN spec

**Candidate follow-ons (documented here, not committed):**
- FM controls (deviation, CTCSS encode, AF filter, limiter) — natives present
- RX/TX graphic + parametric EQ — natives present, no current equivalent for *RX* EQ
- VOX / downward expander (DEXP) + anti-VOX — natives present

**Out of scope:** Diversity RX (needs a 2nd RX channel — architecture), antenna/Alex routing,
SWR protection, serial CAT. These were identified in the gap survey but are separate efforts.

**The good news:** every WDSP function needed below is **already compiled into our native
`libwdsp`** (`native/wdsp/{wcpAGC,ssql,amsq,fmsq,fmd,compress,eq,dexp}.{c,h}`). No native
rebuild is required for AGC/squelch/ALC — only C# P/Invoke bindings + endpoints + UI. (EQ and
VOX would likewise bind to already-present exports.)

---

## 2. ⚠️ Red-Light Items Requiring Maintainer Sign-Off

Per `CLAUDE.md`, defaults an operator feels are red-light. These need Brian's explicit decision
**before** implementation:

| # | Decision | Detail |
|---|----------|--------|
| R1 | ~~AGC max-gain range/default~~ | **RESOLVED — non-issue.** Re-verification of the actual code shows Zeus already uses the Thetis range: backend `SetAgcTop` clamps **-20..120** (`RadioService.cs:1563`), the slider is **0..120** (`AgcSlider.tsx:53`), default 80 (`ApplyAgcDefaults:2975`). The earlier "-20..0" claim was an inventory error. No change needed. |
| R2 | **Default AGC mode** | Thetis default is **MED**; Zeus already hardcodes MED (`WdspDspEngine.cs:2973`). Keep MED as default so first-connect behavior is unchanged. |
| R3 | **Squelch defaults** | All squelch types default **off** in Thetis. Recommend off. Thresholds: SSQL 0.16, AMSQ -150 dB, FMSQ ~1.0. |
| R4 | **ALC/Leveler defaults** | ALC max gain 3 dB / decay 10; Leveler top 15 dB / decay 100; Compressor off. Zeus already ships a leveler max-gain slider (0..15) — confirm we keep its current default. |
| R5 | **Sample-rate ladder** | RESOLVED by maintainer screenshot: ANAN G2 RX1 supports **48 / 96 / 192 / 384 / 768 / 1536 kHz** (RX2 too). The HPSDR wire carries the rate as a `ushort` kHz, so 768/1536 transmit fine (`Protocol2Client.cs:1109`). Gating work is Zeus-side: `HpsdrSampleRate` + `MapSampleRate` cap at 384k today (`RadioService.cs:2243-2260`), and the P2 DDC RX stream + panadapter FFT must be verified at the higher data rates. See §6.5. |
| R6 | **UI placement** | New controls land in a DSP settings area. Visual design/layout is red-light — wireframes below are functional sketches only, not approved visuals. |

---

## 3. Implementation Pattern (the 8 seams)

Every control follows the existing NR/NB path. Reference implementation traced from the
noise-reduction feature. New code copies this exact shape:

1. **Native binding** — `Zeus.Dsp/Wdsp/NativeMethods.cs`: one `[LibraryImport]` +
   `[UnmanagedCallConv(CallConvs=[typeof(CallConvCdecl)])]` partial per WDSP setter. (See
   existing `SetRXAAGCMode` at `NativeMethods.cs:161`, `SetRXASNBARun` at `:406`.)
2. **Engine method** — `Zeus.Dsp/Wdsp/WdspDspEngine.cs` public setter that locks channel
   state, calls natives in order, logs at `LogInformation`; declared on `IDspEngine`.
3. **Service + endpoint** — `RadioService.SetXxx(cfg)` → `Mutate(...)` + `_dspSettingsStore.Upsert(cfg)`
   + `Snapshot()`; `ZeusEndpoints.cs` `MapPost("/api/rx/xxx", ...)`.
4. **DTO** — `Zeus.Contracts/Dtos.cs` config record + set-request record.
5. **Persistence** — `DspSettingsStore.cs`: POCO fields (nullable = "use engine default"),
   `Get()` / `Upsert()`; re-applied on connect.
6. **API client** — `zeus-web/src/api/client.ts` `setXxx(cfg, signal): Promise<RadioStateDto>`.
7. **Store** — `connection-store.ts` field + setter + `applyState` snapshot reconcile.
8. **Pipeline apply** — `DspPipelineService.cs`: latch `_appliedXxx`, change-detect in
   `OnRadioStateChanged`, force-apply at channel init so a fresh engine gets operator choices.

This is the canonical pattern; all four features below plug into it.

---

## 4. Feature Spec — AGC

### 4.0 What's ALREADY in Zeus (verified) — narrows the gap
- **AGC max-gain (top)**: fully wired — 0..120 slider (`AgcSlider.tsx`), `-20..120` backend clamp,
  persisted via `DspSettingsStore.SetAgcTopDb`, `POST /api/agcGain`.
- **Noise-floor Auto-AGC**: already implemented (`RadioService.HandleRxMeterForAutoAgc:1268`) —
  reads RX signal dBm, estimates noise floor, slews an `AgcOffsetDb` (effective AGC-T clamped
  20..100). This is most of §6.6. The explicit ± shift / noise-floor-attack *knobs* aren't
  surfaced yet, but the mechanism exists.
- **Full MED profile at channel open**: `ApplyAgcDefaults` (`WdspDspEngine.cs:2971`) sets
  mode=MED, slope=35, top=80, attack=2, hang=0, decay=250, hangThreshold=100.
- **All AGC natives bound**: `SetRXAAGCMode/Slope/Top/Attack/Hang/Decay/HangThreshold/Fixed`
  already exist in `NativeMethods.cs:161-189`. **No native binding work needed.**

**Therefore the genuine AGC gap is small:** operator control of **mode** (Fixed/Long/Slow/Med/
Fast/Custom) and the **custom params** (slope/decay/hang/hangThreshold/fixed). The max-gain
slider stays as-is and becomes the "Max Gain" field of the new AGC config.

### 4.1 Modes (Thetis `enums.cs:152-162`)
```
FIXD=0, LONG=1, SLOW=2, MED=3 (default), FAST=4, CUSTOM=5
```

### 4.2 WDSP calls (signatures confirmed in `native/wdsp/wdsp.h` / `wcpAGC.h`)
```c
void SetRXAAGCMode(int channel, int mode);
void SetRXAAGCFixed(int channel, double fixed_agc);
void SetRXAAGCTop(int channel, double max_agc);
void SetRXAAGCAttack(int channel, int attack);
void SetRXAAGCDecay(int channel, int decay);
void SetRXAAGCHang(int channel, int hang);
void SetRXAAGCSlope(int channel, int slope);
void SetRXAAGCHangThreshold(int channel, int hangthreshold);
void GetRXAAGCHangLevel(int channel, double *hangLevel);   // optional read-back
```
`SetRXAAGCMode` and `SetRXAAGCTop` are already bound (`NativeMethods.cs:161`); the rest are new.

### 4.3 Ranges / defaults (Thetis `radio.cs`, `setup.cs`)
| Param | Min | Max | Default | Unit | Editable when |
|-------|----:|----:|--------:|------|---------------|
| Fixed gain | -20 | 120 | 20 | dB | mode = FIXD |
| Max gain (Top) | -20 | 120 | 90 | dB | any auto mode — **see R1** |
| Decay | 1 | 5000 | 250 | ms | mode = CUSTOM |
| Hang | 10 | 5000 | 250 | ms | mode = CUSTOM |
| Slope | 0 | 20 | 0 | — | mode = CUSTOM — **note: WDSP receives slope×10** (`setup.cs:9088`) |
| Hang threshold | 0 | 100 | 0 | % | mode = CUSTOM |

### 4.4 Canned-mode presets (applied when mode selected; `console.cs:27960+`)
| Mode | Hang ms | Decay ms | Hang threshold |
|------|--------:|---------:|---------------:|
| LONG | 2000 | 2000 | 100 (on) |
| SLOW | 1000 | 500 | 100 (on) |
| MED  | 0 | 250 | 100 (off) |
| FAST | 0 | 50 | 100 (off) |
| CUSTOM | user | user | user |

> **Implementation note (LONG/SLOW hang threshold):** Thetis couples LONG/SLOW hang-threshold to
> the operator's hang-threshold slider value rather than forcing 100; only MED/FAST force 100.
> Zeus has no per-mode hang-threshold slider memory, so the canned LONG/SLOW path uses a fixed
> **100** (per the table above). This is a deliberate simplification — do not "fix" it back to a
> slider coupling without first adding persisted Custom-threshold memory.

### 4.5 Behavior notes
- Selecting a canned mode pushes its preset hang/decay/threshold; CUSTOM unlocks the controls.
- Thetis does **not** wire `SetRXAAGCAttack` to UI; decay serves as rise-time. We mirror that
  (bind it for completeness but don't surface it).
- Thetis keeps **per-band AGC mode memory**. Phase-2 nicety — Zeus's `BandMemoryStore` is the
  natural home; not required for v1.
- QSK auto-switch (CW break-in temporarily forces CUSTOM tuning): out of scope for v1.

---

## 5. Feature Spec — RX Squelch

Three independent WDSP squelch stages, **auto-selected by RX mode** (Thetis has no squelch-type
dropdown): SSB/CW → SSQL, AM/SAM → AMSQ, FM → FMSQ. UI exposes one "Squelch on/off + threshold"
control whose semantics follow the active mode.

### 5.1 WDSP calls (`wdsp.h`, `ssql.h`, `amsq.h`, `fmsq.h`)
```c
// Voice (SSB/CW)
void SetRXASSQLRun(int channel, int run);
void SetRXASSQLThreshold(int channel, double threshold);   // 0.0..1.0
void SetRXASSQLTauMute(int channel, double tau_mute);      // 0.1..2.0 s
void SetRXASSQLTauUnMute(int channel, double tau_unmute);  // 0.1..1.0 s
// AM
void SetRXAAMSQRun(int channel, int run);
void SetRXAAMSQThreshold(int channel, double threshold);   // -150..0 dB
void SetRXAAMSQMaxTail(int channel, double tail);          // s
// FM
void SetRXAFMSQRun(int channel, int run);
void SetRXAFMSQThreshold(int channel, double threshold);   // ~0..1
```

### 5.2 Ranges / defaults
| Stage | Param | Min | Max | Default |
|-------|-------|----:|----:|--------:|
| SSQL | run | — | — | off |
| SSQL | threshold | 0.0 | 1.0 | 0.16 |
| SSQL | tau mute | 0.1 | 2.0 | 0.1 |
| SSQL | tau unmute | 0.1 | 1.0 | 0.1 |
| AMSQ | run | — | — | off |
| AMSQ | threshold | -150 | 0 | -150 dB |
| AMSQ | max tail | — | — | 1.5 s |
| FMSQ | run | — | — | off |
| FMSQ | threshold | 0 | 1 | 1.0 |

### 5.3 Mapping
A single UI "Squelch" toggle + threshold slider. On toggle, the service applies the run flag to
whichever stage matches the current mode and clears the others. Threshold slider range is
mode-dependent (0..1 for SSQL/FMSQ; -150..0 dB for AMSQ). Tau/tail values use Thetis defaults
unless we later add advanced controls.

---

## 6. Feature Spec — TX Leveling & Bandwidth

### 6.1 ALC (`wcpAGC.h` TXA side)
```c
void SetTXAALCSt(int channel, int state);
void SetTXAALCAttack(int channel, int attack);
void SetTXAALCDecay(int channel, int decay);
void SetTXAALCHang(int channel, int hang);
void SetTXAALCMaxGain(int channel, double maxgain);
```
Thetis exposes only **max gain (0..120 dB, default 3)** and **decay (1..50, default 10)**
(`setup.designer.cs`, `radio.cs:2973`). ALC always runs. Recommend matching: surface max-gain +
decay; leave attack/hang at WDSP defaults.

### 6.2 Leveler
```c
void SetTXALevelerSt(int channel, int state);
void SetTXALevelerTop(int channel, double maxgain);   // 0..20 dB, default 15
void SetTXALevelerDecay(int channel, int decay);       // 1..5000 ms, default 100
```
Zeus already has a leveler max-gain slider (0..15) and `SetTXALevelerSt`-equivalent wiring.
This proposal adds the **decay** control and an explicit on/off, and reconciles the max range
with Thetis (0..20).

### 6.3 Compressor (CPDR) (`compress.h`)
```c
void SetTXACompressorRun(int channel, int run);     // default off
void SetTXACompressorGain(int channel, double gain); // dB
```
New on/off + gain control.

### 6.4 TXA stage order (must preserve)
```
mic → EQ → Leveler → CFIR → CFCOMP(CFC) → Compressor(CPDR) → ALC → Bandpass → IQ out
```
Our `WdspDspEngine` chain comment (`:1360`) already reflects most of this; adding Compressor/ALC
controls must not reorder stages.

### 6.5 Sample rate / bandwidth to ANAN spec
**ANAN G2 RX sample-rate ladder (confirmed from maintainer Thetis F/W Set screenshot):**
`48 / 96 / 192 / 384 / 768 / 1536 kHz` for RX1 **and** RX2. TX DAC stays fixed at 192 kHz.

- **Wire format already supports it.** `ComposeCmdRxBuffer` writes the rate as a big-endian
  `ushort` kHz (`Protocol2Client.cs:1109`); 768 and 1536 fit in a ushort, so no protocol change.
- **Zeus enum caps at 384k.** `HpsdrSampleRate` and `MapSampleRate`/`SampleRateHz`
  (`RadioService.cs:2243-2260`) only map 48/96/192/384. Work item: add `Rate768k` / `Rate1536k`
  to `Zeus.Contracts` `HpsdrSampleRate`, extend both switch maps, and surface in the UI selector.
- **Verification gate (R5):** the higher rates roughly 4×/8× the IQ data rate. Before shipping
  768/1536, verify on the bench: P2 DDC RX stream keeps up (`IqFrame`/ring sizing), the WDSP RX
  channel opens at that rate, the panadapter FFT/usable-span math holds, and CPU is acceptable.
  Treat 768/1536 as **opt-in / experimental** until bench-confirmed on the G2.
- **Usable span ≈ sample_rate × 0.92** (Thetis `console.cs:31391`). Display/zoom already derive
  from rate in Zeus.
- **Per-mode filter limits (Thetis):** TX filter 0..20000 Hz (default 200..3100); CW clamps
  100..2900. RX bandpass via `SetRXABandpassFreqs` (already wired in Zeus), bounded by usable
  span. v1 task: ensure Zeus's filter-edge clamps match Thetis per-mode limits so an operator can
  open filters "out to spec" (e.g. wide AM/FM) without artificial caps.

### 6.6 Auto-AGC with noise-floor compensation (Thetis DSP ▸ AGC/ALC)
The maintainer screenshot shows Thetis's auto-AGC is richer than a simple toggle:
- **Auto AGC RX1/RX2** (*requires panadapter/waterfall* — it reads the displayed noise floor)
- **± Shift** (dB offset above the measured floor; screenshot shows -20)
- **Noise Floor Attack (ms)** (screenshot shows 2000)

**Mostly already built.** `RadioService.HandleRxMeterForAutoAgc:1268` already runs the
floor-tracking loop behind the `/api/auto-agc` toggle. The remaining delta vs Thetis is only the
two exposed knobs — operator **± shift** and **noise-floor attack ms** — which currently live as
constants in that method (`TargetAudioDb`, slew rate, the 500 ms / 5 s tick guards). This is a
small enhancement: lift those constants into the AGC config DTO so the operator can set them.
Lower priority than mode/custom control.

### 6.7 Observed live values (maintainer's G2, reference only — not factory defaults)
From the AGC/ALC screenshot (operator N9WAR's running config): AGC Max Gain **105** (RX1),
Slope 0, Decay/Hang 250, Fixed 20; Leveler enabled Max Gain **10** / Decay 1; ALC Max Gain **2**
/ Decay 1. These confirm the AGC max-gain control routinely runs far above the current Zeus
-20..0 clamp (**R1**) and are useful as sane starting points, but factory defaults (§4.3/§6.1)
remain the Thetis-documented values.

---

## 7. New DTOs / endpoints (summary)

| Endpoint | DTO | Notes |
|----------|-----|-------|
| `POST /api/rx/agc` | `AgcConfig { mode, fixedGainDb, maxGainDb, decayMs, hangMs, slope, hangThreshold }` | replace-style |
| `POST /api/rx/squelch` | `SquelchConfig { enabled, threshold }` | mode-aware service logic |
| `POST /api/tx/leveling` | `TxLevelingConfig { alcMaxGainDb, alcDecayMs, levelerEnabled, levelerDecayMs, compressorEnabled, compressorGainDb }` | **As-built:** the ALC/Leveler/Compressor controls were consolidated into one replace-style endpoint + DTO (mirrors the AGC/squelch pattern) instead of three separate endpoints. ALC has no on/off (St stays 1). |
| `POST /api/tx/leveler-max-gain` | existing | unchanged; clamp widened 0..15 → 0..20 for parity. Leveler max-gain stays on this path (single source of truth), not in `TxLevelingConfig`. |
| `POST /api/sampleRate` | existing | possibly add 384 (R5) |

All persisted via `DspSettingsStore` POCO fields (nullable = use default) and re-applied on
connect via `DspPipelineService` latch/change-detect.

---

## 7a. Settings-Menu Integration (REQUIRED — maintainer directive)

Every new config below must be reachable from the **Settings menu** (`SettingsMenu.tsx`), not
only as inline panel controls. Mirror Thetis's **DSP** Setup tab structure (sub-sections:
AGC/ALC, AM/SAM, FM, NR/ANF, NB/SNB, VOX/DE, CFC, …).

Plan: add a **"DSP" settings tab** to `SettingsMenu.tsx` that hosts, as sections:
- **AGC** — mode selector + custom (slope/decay/hang/hang-threshold) + fixed gain + max-gain.
- **Squelch** — enable + level (mode-aware).
- *(Phase 3)* **TX Leveling** — ALC (max gain/decay), Leveler (top/decay/on-off), Compressor (on/gain).
- *(Phase 4)* **Bandwidth** — RX sample-rate ladder (48..1536), per-mode filter-width limits.

Inline quick-controls (the AGC-T slider, an NR/NB cycle, etc.) stay on the main workspace for
fast access; the Settings tab is the full editor. The two read/write the same store + endpoints,
so they stay in sync automatically (optimistic send + `applyState` reconcile). Each phase's UI
work includes wiring its section into this DSP settings tab. Visual layout is red-light — match
existing Settings-tab idiom + `tokens.css` only.

## 8. Phasing

1. **Phase 0 — sign-off:** ✅ Resolved (maintainer: mirror Thetis verbatim).
2. **Phase 1 — AGC:** ✅ **DONE** — full mode + custom controls. Reviewed (APPROVE), tested
   (15 C# + 13 TS). Reference build of the 8-seam pattern. Established the DSP settings tab.
3. **Phase 2 — RX Squelch:** ✅ **DONE** — mode-aware single control + DSP settings tab section.
   Reviewed; fixed one HIGH defect (FMSQ direction, see §5 note); tested (12 C# + TS).
4. **Phase 3 — TX Leveling:** ALC + Compressor + Leveler-decay. ← NEXT
5. **Phase 4 — Bandwidth:** sample-rate ladder ✅ **DONE (ladder)** — `HpsdrSampleRate` extended
   with Rate768k/Rate1536k; RadioService maps + P1 guards (P1 connect rejects >384, live P1
   clamps); endpoint validation + P2 `rateKhz` switch + `MapHpsdrSampleRate` accept the rungs;
   `SampleRate` type + ConnectPanel options gated to P2. 768/1536 are **P2-only** (P1 wire is
   2-bit, `ControlFrame &0x03`) and **experimental — pending on-radio DDC/FFT bench verification
   on the G2** (the streaming path couldn't be exercised here). Per-mode **filter-cap audit
   deferred** (overlaps the active FilterMiniPan WIP).
6. **Follow-ons (separate proposals):** FM controls, RX/TX EQ, VOX.

> **FMSQ gotcha (Phase 2):** Unlike SSQL/AMSQ, WDSP's FM squelch is a *noise* gate — it opens when
> `avnoise < 0.9*threshold` (`fmsq.c:154,251`), so a higher WDSP threshold opens *more* easily. The
> engine inverts the operator level for FM (`(100-level)/100`) so "higher Level = tighter" holds
> across all three stages. Don't "simplify" it back to `level/100`.

Each phase: builds `dotnet build Zeus.slnx`, existing tests pass, and is bench-checked on the
ANAN G2 (and, where the TX path changes, sanity-checked per the drive/PA caution in CLAUDE.md).

---

## 9. Open Questions for Maintainer
- **R1:** Adopt Thetis AGC max-gain range (-20..+120, default 90), replacing the current -20..0
  "top gain" slider? (Recommended: yes — the operator's own G2 runs 105, §6.7.)
- **R5:** Resolved on capability (G2 does 48..1536 kHz). Remaining call: ship 768/1536 as
  opt-in/experimental pending bench verification, or hold at ≤384 until the DDC stream is
  validated at the high rates?
- Auto-AGC (§6.6): ship the ±shift + noise-floor-attack now, or just the existing toggle first?
- Should per-band AGC-mode memory ship in Phase 1 or be deferred?
- Squelch: single auto-by-mode control (Thetis-faithful) vs. exposing tau/tail advanced knobs?

## 10. Appendix — Adjacent ANAN-G2 settings seen in maintainer screenshots (NOT in this batch)
Captured so they aren't lost; each is a separate future proposal:
- **F/W Set ▸ ANAN-G2 Options:** Dither enable, Random enable, MaxRXFreq (60.00). RX2 sample rate.
- **Options-1:** RX/MOX/RF/PTT delays; CW Key-Up/Key-Down delay; Process Priority; Snap Click
  Tune; Zero Beat RIT; CTUN-no-0-beat; ClickTune Drag; Sync RIT/XIT; VFOSync links CTUN;
  Click-Tune/Filter offsets (DIGU 1500 / DIGL 2210 Hz); Custom Title Text.
- **Options ▸ ANAN-G2 Step Atten:** per-RX enable + dB (RX1 20 / RX2 0). Auto-Attenuate RX with
  sec-hold + undo.
- **DSP ▸ Options:** per-mode (SSB/AM, FM, CW, Digital) RX/TX Buffer Size (IQcomp), Filter Size
  (taps), Filter Type (Low Latency / Linear Phase), Filter Window (BH-4 …); Filter Impulse Cache.
- **DSP ▸ NR/ANF, MNF, NB/SNB, VOX/DE, CFC, AM/SAM, FM, EER, CW** tabs — several already in Zeus
  (NR/NB/CFC); MNF multi-notch, VOX/DE, EER remain follow-ons from the §1 candidate list.

# ANAN-G2E (HermesC10) PureSignal over Protocol 1 — Implementation Design

> Status: KB2UKA-approved build (2026-07-01). Amendments by the session lead are at the bottom and OVERRIDE the body where they conflict.

All verification passes are done — including the decisive find that KB2UKA already signed off (in #1249, commit `23c597bc`) on "the external pad is the protection, no force-seed" for this exact board. I have everything needed to write the design.

---

# DESIGN — ANAN-G2E (HermesC10) PureSignal over Protocol 1

**Status:** decision-ready. **Branch base:** `fix/g2e-puresignal-p1` = `origin/develop`. All `file:line` cites below were re-read in this worktree unless marked from a research report. Items marked **INFERRED** must be re-verified during implementation.

## 0. Core decision — reuse the HL2 P1 PS skeleton, not a new path

The G2E's P1 gateware is *classic Hermes v3.3* (Hermes.v:365 `Hermes_serialno = 8'd33`), and classic Hermes is the **origin** of the exact 4-DDC PS layout Zeus already ships (bench-proven, #566) for the HL2:

| DDC | G2E gateware (VERIFIED) | Zeus HL2 handler today (VERIFIED) |
|---|---|---|
| DDC0 (RX1) | `temp_ADC`, host NCO (Hermes.v:1405) | → `OnIqFrame`: display + RX audio stay alive (Protocol1Client.cs:399-441) |
| DDC1 (RX2) | `temp_ADC`, host NCO | discarded |
| DDC2 (RX3) | `temp_ADC` **always** — the relay-routed feedback tap during TX; host must tune it to TX freq | → pscc **rx** (Protocol1Client.cs:445-449, "psrx=2") |
| DDC3 (RX4) | muxed to `{DACD,2'b00}` + auto-retuned to TX phase word when `FPGA_PTT & PureSignal_enable` (Hermes.v:1401-1402) | → pscc **tx** (Protocol1Client.cs:447-450, "pstx=3") |

Thetis P1 uses the identical index mapping for Hermes/HermesC10 grouped in one case (console.cs:8634-8674, `psrx=2, pstx=3`; report 2, VERIFIED). **Zeus's `HandlePs4DdcPacket` + `TryParseHl2Ps4DdcPacket` (26-byte slot, 38 samples/pkt — the generic `504/(6·4+2)` Hermes framing) can be reused wholesale.** The differences from HL2 are three wire behaviors the G2E needs and the HL2 doesn't. That's the whole feature.

One semantic upgrade for free: on HL2, DDC2 is *radiated leakage* (no coupler); on the G2E it's a real relay-routed sampler tap — deterministic feedback, better than the template it reuses.

## 1. Receiver layout during P1 PS+MOX (requirement 1)

- **Count:** 4 receivers **only while `psArmed && MOX`** (`numRxMinus1 = 3`, Config C4[5:3] — ControlFrame.cs:671-672). Outside PS+MOX: single DDC, byte-identical to today. This mirrors the existing HL2 gate at Protocol1Client.cs:898 (VERIFIED) rather than Thetis's always-4-DDC (console.cs:8318), because Thetis's shape would change the G2E's non-PS wire for no benefit.
- **Feedback = DDC2 (RX3)**, **TX DAC reference = DDC3 (RX4)** — per the table above.
- **RX3 NCO = TX frequency, no new code:** Zeus writes the *same* `VfoAHz` into TxFreq and all four RX NCOs (ControlFrame.cs:334-341, VERIFIED — "Zeus has no separate TX VFO"), so RX3 is on the TX frequency by construction. RX4's NCO is force-overridden in gateware anyway (Hermes.v:1402). *Constraint to document in code: if P1 split-TX ever lands, RX3 must follow the TX VFO.*
- **User RX / display stays alive:** DDC0 keeps streaming (gateware never gates EP6 on PTT — report 1 Q5, VERIFIED) and `HandlePs4DdcPacket` publishes DDC0 → `OnIqFrame` → `MaybeTickInline` (Protocol1Client.cs:399-441; DspPipelineService.cs:5276-5279). **No display starvation. See §6.**
- **Sample rate:** do **not** copy Thetis's whole-wire 192 kHz flip at key-down (report 2 Q5). Zeus HL2 P1 PS ships without it. Open risk: WDSP pins `SetPSFeedbackRate(id, 192_000)` at TX-channel open (Zeus.Dsp/Wdsp/WdspDspEngine.cs:1886, VERIFIED), so feedback arriving at a non-192k wire rate is rate-mismatched into pscc. This is a **pre-existing HL2 condition, not new** — first bench should run the wire at 192 kHz, and the PR must flag it (§4).

## 2. Exact wire bytes (requirement 2)

### (a) PS receiver mux enable — C0=0x14 (reg 0x0A), **C2 bit 6**
Gateware: `PureSignal_enable <= IF_Rx_ctrl_2[6]` under addr `0001_010` (Hermes.v:2170-2173, report 1 VERIFIED). This is the **same bit** Zeus already sets for HL2 at ControlFrame.cs:510-513 (VERIFIED: `if (s.Board == HermesLite2 && s.PsEnabled) c14[1] |= 1 << 6`). **Change: widen the gate to `HermesLite2 || HermesC10`.** Set whenever `PsEnabled` (armed at rest is safe — the gateware mux acts only under `FPGA_PTT`, Hermes.v:1401). Co-tenant fields on this frame (LineIn gain C2[4:0], attenuator C4) are composed single-pass in the same writer (ControlFrame.cs:402-475), so no clobber risk; HermesC10 keeps the non-HL2 C4 encoding `0x20 | db` untouched.

### (b) RX BYPASS relay — Config C0=0x00, **C3[6:5] = 01**, only while armed+keyed
Gateware (all VERIFIED in this pass): `IF_RX_relay <= IF_Rx_ctrl_3[6:5]` (Hermes.v:2144); `C122_Rx_1_in = (IF_RX_relay == 2'b01)` (Hermes.v:2474); on the Mk2PA build the Alex SPI word places `C122_Rx_1_in` at **bit 11 = RX BYPASS OUT** (Hermes.v:2492-2494; SPI_Mk2PA.v bit table) — the same physical relay the P2 fix routes via alex0 bit 11.

**Discrepancy, called:** Thetis additionally sets C3[7] (`_Rx_1_Out`, networkproto1.c:450-461). I verified in this pass that on the **Mk2PA build C3[7] → `IF_Rout` is decoded but never used** — `C122_Rx_1_out` appears only in the *non*-Mk2PA `else` Alex word (Hermes.v:2468, 2509 vs the `ifdef HERMES_MK2PA` word at 2492-2494). **Decision: emit C3[6:5]=01 only, not C3[7]** — gateware over Thetis, byte-minimal.

**Predicate:** `board == HermesC10 && s.PsEnabled && s.Mox` — **deliberately independent of `PsFeedbackSource`**, mirroring #1249 commit `ab30ee59` ("route the G2E external feedback tap regardless of the Internal/External pick — Internal is a physically-impossible source on this board", VERIFIED via `git show`). At every unkey the rotation re-sends Config with MOX=0 and the operator's RX antenna restored via the existing `EncodeRxAntennaC3Bits` path (ControlFrame.cs:700-712) — the gateware has no PTT term on this relay (report 1 Q5), so the host-driven dance is mandatory and the continuous C&C rotation provides it. Note the field overlap: the bypass value `0b001 << 5` occupies the same C3[7:5] the antenna encoder writes — implement as a PS-override branch *inside* `WriteConfigPayload`/a sibling pure helper so exactly one writer owns C3[7:5] per frame.

### (c) TX-time ADC attenuation — C0=0x1C (reg 0x0E), **C3[4:0]**, 0–31 dB
Gateware (report 1 Q4, VERIFIED): `atten_on_Tx <= IF_Rx_ctrl_3[4:0]` (Hermes.v:2187); mux `attenuator = FPGA_PTT ? atten_on_Tx : atten_data` (Hermes.v:2278); **silicon reset default = 31 dB** (Hermes.v:2127). So *not writing this register = the P2 starved-tap bug as the factory state.* Zeus must write it.

**Collision found and resolved (verified this pass):** wire byte 0x1C is already `CcRegister.LnaTxGainStable` in Zeus, and its payload writer sends **all zeros** (ControlFrame.cs:516-545). Blindly reusing the HL2 PS rotation for HermesC10 would command **atten_on_Tx = 0 dB** — the opposite extreme (ADC clip risk on a hot tap). **Decision: keep the register slot, board-branch the payload writer** — HL2 → zeros (unchanged, byte-identical); HermesC10 → `C3 = clamp(psTxAttnDb, 0, 31)`; sentinel-unset → emit **31** (= hardware reset value, honest no-op). The register is only ever scheduled in the PS-armed rotation, so no other board ever emits it.

**Default + operator flow:** value = the operator's persisted `PsTxAttnByBoard["p1:HermesC10"]` (key mechanism VERIFIED: RadioService.cs:819-825; Bob's stored 31 will be honored) — restored to the client at connect exactly like the HL2 restore at DspPipelineService.cs:3329-3335, surfaced/edited via the existing `/api/tx/ps/feedback-attenuation` → `SetPsFeedbackAttenuationDb` (DspPipelineService.cs:3236-3250, add a `HermesC10-on-P1` branch), and walked by the auto-attenuate dance when the operator's existing toggle is on (§3, PsAutoAttenuateService). **Fresh-install default: 0 dB** — this is *not* my invention: KB2UKA already signed off on exactly this rationale for this exact board in #1249 `23c597bc` ("the external pad is the protection… arming PS on the G2E leaves byte 59 at the operator's value", VERIFIED), and develop's default for the p1 key is already 0 (RadioService.cs:4022 `GetPersistedPsTxAttnDb() ?? 0`). Thetis parity would be 31 (report 2 Q4) — rejected as the extreme that deadlocks auto-attenuate at fb-zero (the documented #1249 failure). No force-seed in either direction, ever. Write the register throughout the armed rotation (RX and MOX phases) so the value is pre-positioned before the first key-down replaces the silicon 31.

### (d) Receiver count — Config C4[5:3] = 3 during PS+MOX only
Join the HL2 gate at Protocol1Client.cs:898. Gateware requirement `IF_last_chan = 3` for RX4 to stream: Hermes.v:2151 (report 1, VERIFIED).

## 3. Zeus change list (requirement 3)

Gate everywhere: `board == HpsdrBoardKind.HermesC10` (protocol implicit — this is the P1 client), plus `PsEnabled` / `Mox` as listed. `HermesC10` already round-trips P1 discovery (0x14 → ReplyParser.cs:140, report 3).

| # | File | Change | Gate | Byte-identical guarantee for others |
|---|---|---|---|---|
| 1 | `Zeus.Protocol1/ControlFrame.cs` | (i) PS bit: widen :510-513 to `HL2 \|\| HermesC10`. (ii) Config C3 bypass override (new pure helper beside `EncodeRxAntennaC3Bits` :700). (iii) `WriteLnaTxGainStablePayload` :516 board-branch: HermesC10 → `C3 = attn`, sentinel→31. (iv) `CcState`: add `int PsTxAttnOnTxDb = int.MinValue` (do **not** reuse `Hl2TxAttnDb` — different range/register/semantics, ControlFrame.cs:238-244) | (i) `PsEnabled`; (ii) `PsEnabled && Mox`; (iii) register only reachable from C10 PS rotation | Every branch names `HermesC10` explicitly; HL2 branches untouched; goldens (§5) |
| 2 | `Zeus.Protocol1/Protocol1Client.cs` | (i) `SnapshotState` :898 `numRxMinus1`: `psOn && (isHl2 \|\| isC10) && moxOn`; plumb PsTxAttn into CcState. (ii) RxLoop 4-DDC gate :1040-1044: add HermesC10. (iii) TxLoop `psArmed` :1350: add HermesC10 (same 16-phase rotation — the `LnaTxGainStable` slots now carry atten_on_Tx via the board-branched writer; `RxFreq3/4` slots already there). (iv) new setter `SetPsTxAttenOnTxDb(int)` clamp 0..31, thread-safe field like :845-852. (v) `HandlePs4DdcPacket`: add mic extraction (see mic note below) | (i-iii) as shown | Non-PS boards keep the 5-phase rotation (`psArmed` false); numRx stays 0 |
| 3 | `Zeus.Protocol1/IProtocol1Client.cs` | declare the new setter | — | additive |
| 4 | `Zeus.Protocol1/PacketParser.cs` | `ExtractMicSamples4Ddc` for the 26-byte-slot frame (mic bytes at slot offset 24-25 — **INFERRED** from the `3I+3Q ×4 + 2 mic` layout, PacketParser.cs:369; verify against Hermes.v EP6 assembly) | called only from the C10 4-DDC path when `_radioMicHandler != null` (mirrors :1131-1140) | new code, no existing path touched |
| 5 | `Zeus.Server.Hosting/DspPipelineService.cs` | (i) `psEngineSupported` :3828-3834: `\|\| board == HermesC10` — the GH #426 freeze rationale ("no possible feedback source") no longer holds for C10. (ii) `SetPsFeedbackAttenuationDb` :3236-3250: `HermesC10 && ActiveClient != null` branch → clamp 0..31 → `p1.SetPsTxAttenOnTxDb` + persist. (iii) connect restore :3329-3335: add C10 branch | connected board check | Other P1 boards still skip engine arm (GH #426 guard intact) |
| 6 | `Zeus.Server.Hosting/PsAutoAttenuateService.cs` | Dispatch :483-495: insert `else if (board == HermesC10 && p1 != null) → Tick1HermesC10P1` before the p2-null skip. New dance = clone of `Tick1P2` (range 0..31, thresholds 128/181/152.293 at :56-60 unchanged) with the P1 wire write; port #1249's stall-acquisition (`1375a382`) semantics | `ConnectedBoardKind == HermesC10 && ActiveClient != null` | HL2 branch first and unchanged; other P1 boards still hit `skip=p2-null` |
| 7 | `RadioService.cs` | **No change required.** `ResolvePsHwPeak (false,_) → 0.4072` already covers p1:HermesC10 (RadioService.cs:3967); `attnMin = 0` correct (:4021); board key exists (:819-825). Fix the stale comment ":3957 'P1 today is gated off in the frontend'" while there | — | — |
| 8 | Frontend | **Zero changes.** PsToggleButton, attenuation control, auto-attenuate toggle and PS Monitor already render for this board (report 3 Q5) — "UI must not regress" is satisfied by not touching it | — | — |

**Mic note (works-on-first-try item):** `HandlePs4DdcPacket` today mirrors PTT/CW/telemetry but **not** mic (verified: no `ExtractMicSamples` call in :351-512) — fine on HL2 (no codec), but the G2E P1 gateware is classic Hermes with a TLV320 codec, and Zeus has a radio-mic feature (`P1RadioMicReceiver`, extraction gated on `_radioMicHandler`, Protocol1Client.cs:1131-1140). A G2E operator on the radio-mic source would lose TX audio the instant PS keys. **Whether the G2E chassis actually wires the front mic to the C10's TLV320 is INFERRED** — verify in the gateware/board docs first; if it does, item 4 is mandatory; if not, drop it (YAGNI) and note why.

**PS hard-rule compliance:** `PsEnabled` init/persistence, `/api/tx/ps`, `PsSettingsStore`, `PersistPsState`, `HwPeakByBoard` defaults — all untouched. The arm/disarm sequencing (100 ms settle, keyed-arm deferral, DspPipelineService.cs:3774-3850) is reused as-is; C10 inherits the deferred-arm-while-keyed protection automatically. This PR still needs KB2UKA's explicit pre-approval as a PureSignal change — it cannot ship under autonomous green-light rules.

## 4. Not building (YAGNI) + bench-only risks (requirement 4)

**Not building:** the 2-DDC path (`TryParse2DdcPacket` stays caller-less — 4-DDC matches gateware+Thetis and reuses the proven handler); Thetis's whole-wire 192k flip; any `PsFeedbackSource` wiring on P1 (mirrors `ab30ee59`); C3[7] emission; split-TX RX3 retune; HermesII/10E P1 arm (stays byte-identical, mirrors #1249's explicit 10E carve-out); hw-peak auto-cal changes; any UI.

**Bench-only risks (PR must flag):**
1. **Bypass jack → ADC with `RX_MASTER_IN_SEL`=0** — gateware only auto-asserts bit 14 for XVTR/EXT1, not bypass (Hermes.v:2489). P2 bench success with bit 11 alone strongly suggests OK; **INFERRED** for P1.
2. **Bob's flashed 3.3 binary = mirrored source** — serialno matches; **INFERRED**.
3. **WDSP feedback rate pinned 192k** (WdspDspEngine.cs:1886) vs operator wire rate — shared HL2 condition; bench at 192k first, then characterize.
4. **hw_peak 0.4072 default** vs Thetis's 0.2899 (cmaster.cs:541) — operator-calibratable already; bench-tune.
5. **Bob's persisted 31 dB attn** — honored by design; if his 55 dB sampler + 31 dB reads fb-zero, the ported stall-acquisition (item 6) is the recovery; tell him to zero it as first bench step.
6. Mic wiring (above).

## 5. Test plan (requirement 5)

- **Golden byte-identity (the load-bearing suite):** new `ControlFramePsHermesC10GoldenTests` on the `ExternalPortGoldenTests` pattern (per-board `CcState` builders + exact-byte asserts, tests/Zeus.Protocol1.Tests/ExternalPortGoldenTests.cs:33-197). For **every** P1 board except HermesC10 — Hermes, HermesII, Angelia, Orion, OrionMkII, Metis, **and HL2** — assert Config/Attenuator/DriveFilter/0x1C payloads with `PsEnabled=true, Mox=true` are byte-for-byte identical to develop's current expected bytes (HL2's own PS bytes locked to today's values, not to the PS-off baseline).
- **HermesC10 positive locks:** C2[6] set when armed; Config C3 = `0b001<<5` only when armed+keyed and operator antenna restored when not; C4[5:3]=3 only when armed+keyed; 0x1C C3 = attn, sentinel→31, clamp 0/31.
- **Rotation:** `PhaseRegisters` — C10 armed → 16-phase; every other non-HL2 board armed → still 5-phase (extends ControlFramePsEncoderTests:168-179 precedent; note the existing `Attenuator_NonHl2_PsEnabled_Does_Not_Set_C2_Bit6` test at :68 must become "non-HL2-non-C10" — a deliberate, reviewed change).
- **Parser:** existing `TryParseHl2Ps4DdcPacket` tests already pin the framing; add the 4-DDC mic-extraction test if built.
- **Auto-attenuate:** `Tick1HermesC10P1` dispatch + step math + clamp + disable/restore bracket, mirroring the existing Tick1Hl2/Tick1P2 suites; assert non-C10 P1 boards still log `skip=p2-null`.
- **Pipeline:** update the GH #426 guard test to carve out HermesC10 (engine arms; other P1 boards still skip); existing PsEnabled-never-persisted suites run unchanged — they pin the hard-rule invariant.
- **VirtualRadio:** `Zeus.VirtualRadio/P1/` exists — mirror #1249's non-vacuous emulator approach (`6a9800c3`/`ab30ee59`): decode Config C3[6:5], C2[6], and 0x1C in the P1 decoder and assert the real composer → emulator chain routes/starves correctly (audit current P1 decoder coverage first — unaudited, per report gaps).

## 6. Display tick / 14db9d09 (requirement 6)

**Not needed on this path.** DDC0 flows throughout PS+MOX (gateware never PTT-gates EP6 — report 1 Q5; `HandlePs4DdcPacket` publishes DDC0 → `OnIqFrame`, Protocol1Client.cs:399-441 VERIFIED), so the IQ-paced `MaybeTickInline` keeps ticking — the P2 starvation shape (2-DDC, both consumed by PS) does not exist here. `14db9d09` already patches *both* sinks on the #1249 branch (verified: `git branch --contains` → #1249 branches only; diff hunks at both :5154 and :5209). **Do not duplicate it in this PR** — land #1249 first and rebase; this PR's only DspPipelineService edits (§3 items 5.i-iii) are in different regions, but PsAutoAttenuateService overlaps #1249's `1375a382`, so **merge order: #1249 → this PR** is a hard sequencing requirement.

---

**Summary of INFERRED items needing re-verification during implementation:** mic byte position in the 4-DDC EP6 frame + whether the G2E chassis wires a P1 mic at all; bypass-jack-to-ADC path with bit 14 low; Bob's binary = mirrored 3.3 source; emulator P1 decoder coverage baseline. Everything else in §1–§3 is VERIFIED against gateware source, Thetis source, or this worktree at the cited lines.

---

## SESSION-LEAD AMENDMENTS (binding)

1. **Auto-attenuate: do NOT port the #1249 stall-acquire (commit 1375a382).** That logic is a flagged merge blocker on #1249 (one-shot per arm via the no-new-calc gate; wrong-direction walk on cold-fit fb=0). Tick1HermesC10P1 = a clone of the plain Tick1P2 mi0bot walk ONLY (tooHot/tooQuiet, fb>0 gated, disable/restore bracket, clamp 0..31, persist via the existing per-board key). fb-zero recovery is the operator attenuation control; the PR flags this explicitly.
2. **Mic extraction (change-list item 4): verify before building.** Read the EP6 frame assembly in the G2E P1 Hermes.v (Tx_fifo / EP6 packer) to confirm mic bytes exist per sample slot in the 4-DDC layout and their offset. If present, build ExtractMicSamples4Ddc gated on the existing _radioMicHandler pattern; if absent, skip and record why here.
3. **Gateware verification is DONE for:** 0x14/C2[6] PureSignal_enable (Rx4 DACD mux, PTT-gated), 0x1C/C3[4:0] atten_on_Tx (reset 31), Config C3[6:5]=01 -> C122_Rx_1_in -> Mk2PA SPI bit 11, bit 14 auto-asserts only for XVTR/EXT1, C3[7] unused on Mk2PA, IF_last_chan=C4[5:3]. Cites re-read by the session lead in Hermes_3.3_C10_P1_Mk2PA/Hermes.v.

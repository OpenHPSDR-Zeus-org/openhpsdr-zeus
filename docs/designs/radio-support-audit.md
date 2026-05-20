# Zeus Per-Board Support Audit

**Audit Date:** 2026-05-04
**Auditor:** zeus-auditor
**Coverage:** HpsdrBoardKind enums, discovery mappings, drive profiles, PA defaults, calibrations, wire format quirks

> **Status update:** every "Critical Gap" called out below was closed by
> the six-phase implementation tracked in
> `docs/designs/radio-support-plan.md`. This document is preserved as the
> historical snapshot that guided the implementation; live truth is in
> the plan doc + the source.
>
> - **Gap 1** (P1/P2 enum split) — closed by Phase 4 (`932c040`); single
>   `Zeus.Contracts.HpsdrBoardKind` covers both protocols.
> - **Gap 2** (0x0A alias collision) — closed by Phase 3 (`d807611`);
>   operator selects variant via `OrionMkIIVariant` and dispatch routes
>   to the right calibration / PA-gain bucket.
> - **Gap 3** ("OrionMkII not in `PaDefaults.TableFor`") — was already
>   wired before the audit ran (see `PaDefaults.cs:102` at the time);
>   audit reading was stale.
> - **Secondary gaps 4–7** — all addressed in the plan's executed phases.

---

## Current HpsdrBoardKind Enum Values

Zeus defines board enums **separately for P1 and P2**, creating a protocol-specific split:

### Protocol 1 (Zeus.Protocol1/Discovery/HpsdrBoardKind.cs)

| Value | Enum Member | Wire ID | Notes |
|-------|-------------|---------|-------|
| 0x00  | Metis       | 0x00    | Protocol-1 only; no P2 equivalent |
| 0x01  | Hermes      | 0x01    | ✓ Both P1 & P2 |
| 0x02  | Griffin     | 0x02    | Protocol-1 only; no P2 equivalent |
| 0x04  | Angelia     | 0x04    | ✓ Both P1 & P2 |
| 0x05  | Orion       | 0x05    | ✓ Both P1 & P2 |
| 0x06  | HermesLite2 | 0x06    | ✓ Both P1 & P2 |
| 0x0A  | OrionMkII   | 0x0A    | ✓ Both P1 & P2; **aliases ANAN-8000D and G2** |
| 0xFF  | Unknown     | 0xFF    | Fallback |

### Protocol 2 (Zeus.Protocol2/Discovery/HpsdrBoardKind.cs)

| Value | Enum Member | Wire ID | Notes |
|-------|-------------|---------|-------|
| 0x00  | Atlas       | 0x00    | Protocol-2 only; no P1 equivalent |
| 0x01  | Hermes      | 0x01    | ✓ Both P1 & P2 |
| 0x02  | HermesII    | 0x02    | Protocol-2 only; Griffin alternative |
| 0x04  | Angelia     | 0x04    | ✓ Both P1 & P2 |
| 0x05  | Orion       | 0x05    | ✓ Both P1 & P2 |
| 0x06  | HermesLite2 | 0x06    | ✓ Both P1 & P2 |
| 0x0A  | OrionMkII   | 0x0A    | ✓ Both P1 & P2; aliases G2 and ANAN-8000D |
| 0xFF  | Unknown     | 0xFF    | Fallback |

---

## Discovery Mapping Coverage

### Protocol 1 (Zeus.Protocol1/Discovery/ReplyParser.cs:119–129)

```csharp
private static HpsdrBoardKind MapBoard(byte raw) => raw switch
{
    0x00 => HpsdrBoardKind.Metis,        // ✓ mapped
    0x01 => HpsdrBoardKind.Hermes,       // ✓ mapped
    0x02 => HpsdrBoardKind.Griffin,      // ✓ mapped
    0x04 => HpsdrBoardKind.Angelia,      // ✓ mapped
    0x05 => HpsdrBoardKind.Orion,        // ✓ mapped
    0x06 => HpsdrBoardKind.HermesLite2,  // ✓ mapped
    0x0A => HpsdrBoardKind.OrionMkII,    // ✓ mapped
    _ => HpsdrBoardKind.Unknown,
};
```

**Recognition:** 7 boards recognized; unknown discovery IDs fall through to `Unknown`.

### Protocol 2 (Zeus.Protocol2/Discovery/ReplyParser.cs:111–121)

```csharp
private static HpsdrBoardKind MapBoard(byte raw) => raw switch
{
    0x00 => HpsdrBoardKind.Atlas,        // ✓ mapped
    0x01 => HpsdrBoardKind.Hermes,       // ✓ mapped
    0x02 => HpsdrBoardKind.HermesII,     // ✓ mapped
    0x04 => HpsdrBoardKind.Angelia,      // ✓ mapped
    0x05 => HpsdrBoardKind.Orion,        // ✓ mapped
    0x06 => HpsdrBoardKind.HermesLite2,  // ✓ mapped
    0x0A => HpsdrBoardKind.OrionMkII,    // ✓ mapped
    _ => HpsdrBoardKind.Unknown,
};
```

**Recognition:** 7 boards recognized; unknown discovery IDs fall through to `Unknown`.

---

## Per-Board Seam Coverage

### 1. Drive Profile (Zeus.Server.Hosting/RadioDriveProfile.cs:195–199)

**Dispatch Method:** `RadioDriveProfiles.For(HpsdrBoardKind board)`

| Board | Profile | Encoding | Tested |
|-------|---------|----------|--------|
| HermesLite2 | HermesLite2DriveProfile | 4-bit nibble (0..15 steps) | Yes (docs/lessons/hl2-drive-model.md) |
| All others | FullByteDriveProfile | Full 8-bit (0..255) | Yes (piHPSDR/Thetis reference) |

**Key Quirk:** HL2 uses a percentage-based model (0–100 per-band output %), NOT decibels. The `paGainDb` parameter is overloaded: dB for FullByte boards, % for HL2. See HermesLite2DriveProfile.EncodeDriveByte():165–184 for the 4-bit quantisation math.

**Entry Point:** RadioService.RecomputePaAndPush():921 uses `RadioDriveProfiles.For(ConnectedBoardKind)`.

### 2. PA Defaults (Zeus.Server.Hosting/PaDefaults.cs:95–138)

**Dispatch Method:** `PaDefaults.TableFor(HpsdrBoardKind board)` and `GetPaGainDb(board, band)`

| Board | Table | Per-Band Gains | Rated Watts | Tested |
|-------|-------|---|---|---|
| Hermes | HermesGains | 38.8–41.3 dB | 10 W | Yes |
| Metis | HermesGains | 38.8–41.3 dB | 10 W | Yes |
| Griffin | HermesGains | 38.8–41.3 dB | 10 W | Yes (no dedicated table) |
| Angelia | Anan100Gains | 42.0–50.5 dB | 100 W | Yes |
| Orion | Anan200Gains | 43.5–50.5 dB | 100 W | Yes |
| OrionMkII | *Not dispatched* | **∅** | 100 W | No explicit table |
| HermesLite2 | Hl2OutputPct | 38.8–100.0 % | 5 W | Yes (% not dB) |
| Unknown / fallback | `{}` empty dict | 0.0 (legacy mode) | 0 W | — |

**Key Quirk:** HermesLite2 uses output-percentage (0..100), not dB. HF bands = 100 %, 6 m = 38.8 %. All other boards use dB forward-gain (Thetis/piHPSDR convention).

**Gap:** OrionMkII has no explicit dispatch in `TableFor()` — falls through to empty dict, losing per-band calibration on PA Settings load. See lines:96–104.

### 3. Meter Calibration (Zeus.Server.Hosting/RadioCalibrations.cs:50–69)

**Dispatch Method:** `RadioCalibrations.For(HpsdrBoardKind board)`

| Board | Calibration Bucket | Bridge V | Ref V | ADC Offset | MaxW | Notes |
|-------|---|---|---|---|---|---|
| HermesLite2 | RadioCalibration.HermesLite2 | 1.5 V | 3.3 V | 6 | 5 W | ✓ |
| Hermes | RadioCalibration.Hermes | 0.09 V | 3.3 V | 6 | 10 W | ✓ |
| Metis | RadioCalibration.Hermes | 0.09 V | 3.3 V | 6 | 10 W | ✓ |
| Griffin | RadioCalibration.Hermes | 0.09 V | 3.3 V | 6 | 10 W | ✓ |
| Angelia | RadioCalibration.Anan100 | 0.095 V | 3.3 V | 6 | 100 W | ✓ |
| Orion | RadioCalibration.Anan200 | 0.108 V | 5.0 V | 4 | 200 W | ✓ |
| OrionMkII | RadioCalibration.OrionMkII | 0.12 V | 5.0 V | 32 | 100 W | ✓ G2 (default) |
| Unknown | RadioCalibration.HermesLite2 | — | — | — | — | Fallback to HL2 |

**Load-bearing note:** Board id 0x0A aliases two different hardware buckets:
- **ANAN-7000DLE / G1 / G2 / G2-1K / RedPitaya** (Thetis ANAN_G2) → bridge 0.12
- **ANAN-8000D** (Thetis ORIONMKII) → bridge 0.08 (**~30 % difference**)

The dispatch defaults to G2 (line:67) because KB2UKA's test rig is a G2 MkII. ANAN-8000D operators may see ~30 % low forward-power reading. An alternate bucket `RadioCalibration.OrionMkIIAnan8000` exists (lines:134–138) but is not wired.

**TODO:** RadioCalibrations.cs:64–66 flags `TODO(p2-cal): expose discovery byte / firmware string so the ANAN-8000D bucket can be chosen automatically`.

### 4. Wire Format Quirks (Zeus.Protocol1/ControlFrame.cs)

HL2-specific control-frame encoding; all other boards use standard Protocol-1 semantics.

| Register | HL2 Quirk | Line |
|----------|-----------|------|
| DriveFilter (0x12) | C2 \|= 0x08 for PA enable during MOX | 208–211 |
| Attenuator (0x14) | C4 = 0x40 \| (60 - Db); RX attn = inverse gain | 241–243 |
| Attenuator (0x14) | TX step-atten override during MOX + PS active | 257–262 |
| Attenuator (0x14) | PureSignal enable → C2 bit 6 | 277–280 |
| LnaTxGainStable (0x1c) | All zeros → en_tx_gain=0; AD9866 PGA holds RX gain across MOX (stable PS feedback). Note: mi0bot's historical "cntrl1=0x04 → DDC1→ADC1" reading does NOT match upstream HL2 gateware — see ControlFrame.cs:LnaTxGainStable and hermes-lite2-protocol.md "Feedback IQ". | 295–296 |
| Predistortion (0x2b) | Subindex in C1, value [3:0] in C2 | 311–312 |
| Config (0x00) | N2ADR filter-board auto-OC mask | 330–333 |
| IQ Payload | TX IQ written only for HL2 during MOX | 421–437 |

**Guard:** All HL2 wire-format logic is guarded by `state.Board == HpsdrBoardKind.HermesLite2`; non-HL2 boards never see these bits set, so no cross-contamination.

### 5. PureSignal Hardware Peak (Zeus.Server.Hosting/RadioService.cs:1183–1197)

Per-board PS hardware-peak defaults. Applies to both RXA/TXA WDSP engine scaling and meter-display scaling.

| Board | P1 Value | P2 Value | Notes |
|-------|----------|----------|-------|
| HermesLite2 | 0.233 | 0.233 | Same across protocols |
| All other P2 | — | 0.2899 | Protocol-2 default |
| OrionMkII (P2 only) | — | 0.6121 | G2-specific override |
| All other P1 | 0.4072 | — | Protocol-1 default |
| Unknown | — | — | Falls through to P2 default (0.2899) |

**Source authority:** Thetis clsHardwareSpecific.cs:295–328, pihpsdr transmitter.c:1166–1179 (Saturn/G2).

**TODO:** RadioService.cs:1190 flags `TODO(ps-p1): P1 path is deferred — only P2 is wired through to Protocol2Client.SetPsFeedbackEnabled`.

### 6. Board Kind Resolution (Zeus.Server.Hosting/RadioService.cs:1298–1335)

**ConnectedBoardKind property (line:1298):**
- If P1 client is active → return P1 client's `BoardKind` from discovery
- If P2 active (no P1) → return `OrionMkII` as a placeholder (hardcoded default)
- If disconnected → return `Unknown`

**EffectiveBoardKind property (line:1329):**
- If connected → use `ConnectedBoardKind` (discovery wins)
- If disconnected but preference set → use preferred board (operator dropdown)
- If no preference → return `Unknown`

**Gap:** When P2 is active and no P1 fallback is available, the server returns `OrionMkII` unconditionally (line:1318). This is a placeholder that works for G2/Saturn but misses P2-only boards like `Atlas` or `HermesII`.

---

## Test Coverage

### EffectiveBoardKindTests (tests/Zeus.Server.Tests/EffectiveBoardKindTests.cs)

- ✓ Disconnected with no preference → Unknown
- ✓ Disconnected with preference → use preferred
- ✓ Preference defaults PA table before connect
- ✓ Null PreferredStore gracefully falls back
- **Gap:** No test for P2-active fallback when disconnected

### RadioCalibrationsDispatchTests (tests/Zeus.Server.Tests/RadioCalibrationsDispatchTests.cs)

- ✓ HermesLite2 → HL2 cal
- ✓ Hermes / Metis / Griffin → Hermes cal
- ✓ Angelia → Anan100 cal
- ✓ Orion → Anan200 cal
- ✓ OrionMkII → OrionMkII cal (G2, not ANAN-8000D)
- ✓ Unknown → HL2 cal (fallback)
- **Gap:** No test for ANAN-8000D discrimination (the alternate bucket exists but is never used)

---

## Summary of Gaps

### Top 3 Critical Gaps

1. **Board Enum Split (P1 vs P2)**
   - Protocol 1 has `Metis`, `Griffin` (no P2 equivalents)
   - Protocol 2 has `Atlas`, `HermesII` (no P1 equivalents)
   - Server-side code assumes a single `HpsdrBoardKind` — currently uses `Zeus.Protocol1.Discovery.HpsdrBoardKind`
   - **Risk:** P2-only boards (`Atlas`, `HermesII`) discovered on Protocol 2 are mapped back to P1 enums, then fall through to Unknown in server dispatch
   - **Mitigation needed:** Unified server-side enum or per-protocol dispatch wrapper

2. **OrionMkII Alias Collision (0x0A)**
   - Single board id 0x0A covers: ANAN-7000DLE, ANAN-G1, ANAN-G2, ANAN-G2-1K, **ANAN-8000D**, RedPitaya
   - Drive profile, PA defaults, and calibrations default to G2 (bridge 0.12)
   - ANAN-8000D has different calibration (bridge 0.08 — ~30 % meter error)
   - No discriminator available in discovery reply (firmware version or board-specific string needed)
   - **Impact:** ANAN-8000D operators see ~30 % low FWD power reading until discriminator added
   - **Mitigation:** TODO(p2-cal) at RadioCalibrations.cs:64–66

3. **PA Defaults Missing for OrionMkII**
   - `PaDefaults.TableFor()` line:103 has no case for `OrionMkII`
   - Dispatch falls through to empty dict → all per-band values = 0.0 dB (legacy mode)
   - On first connect to a G2, the PA Settings panel shows all zeros instead of 47.9/50.9 dB seeds
   - **Impact:** Operator must manually enter per-band PA gains on first use
   - **Mitigation:** Add OrionMkII dispatch in PaDefaults.TableFor() to use OrionG2Gains table (lines:67–72)

### Secondary Gaps

4. **P2 Placeholder for ConnectedBoardKind**
   - When P2 active and no P1 fallback, code returns `OrionMkII` hardcoded (RadioService.cs:1318)
   - Should reflect actual discovered board (`Atlas`, `HermesII`, etc.)
   - **Risk:** Wrong PA table loaded on P2-only discovery; wrong wire-format encoding attempted
   - **Mitigation:** P2 discovery reply must carry enough info to map back to server-side board kind

5. **No DriveProfile for ANAN-8000D**
   - All non-HL2 boards use `FullByteDriveProfile` (8-bit)
   - If ANAN-8000D ever needs a different drive model (e.g., different PA gain ceiling), it must be added via `RadioDriveProfiles.For()` dispatch
   - **Currently OK:** All 8-bit boards share the same drive math (Thetis/piHPSDR convention)

6. **Test Gap: P2 Board Resolution**
   - No unit test verifies that P2-discovered boards (`Atlas`, `HermesII`) are correctly resolved when P1 is inactive
   - EffectiveBoardKindTests assumes P1 only

7. **PureSignal P1 Deferred**
   - `ResolvePsHwPeak()` has P1 cases defined but marked TODO(ps-p1) at line:1190
   - P1 PureSignal path is not wired through to Protocol1Client
   - Only affects radios that support both P1 and PS (primarily HL2)

---

## Anomalies & Load-Bearing Notes

### Anomaly: HL2 PA Model Semantics Overload
- **Field name:** `PaGainDb` (stored in DTO)
- **P1 Hermes/ANAN/Orion interpretation:** dB forward gain (Thetis/piHPSDR)
- **HL2 interpretation:** Per-band **output percentage** (0..100), NOT dB
- **Resolution point:** Inside `IRadioDriveProfile.EncodeDriveByte()` — each profile interprets the parameter differently
- **Correctness:** ✓ Properly documented in RadioDriveProfile.cs:66–72; HermesLite2DriveProfile.cs:131–156

### Anomaly: HL2 TX Attenuation During PureSignal
- ControlFrame.WriteAttenuatorPayload() line:257–262 switches C4 source from RX step-atten to **TX PGA** during MOX + PS
- TX attn is a separate feedback-path PGA (AD9866 TX DAC → PA tap → feedback ADC); not a wire-side step attenuator
- **Risk:** If this logic is ported to a board without a dedicated feedback PGA, silent TX-attn failure
- **Guard:** Line:257 checks `state.Board == HpsdrBoardKind.HermesLite2` explicitly

### Load-Bearing: Metis/Griffin Treated as Hermes
- No PaDefaults or RadioCalibrations buckets for Metis / Griffin
- Both dispatch to `Hermes` bucket in calibrations, `HermesGains` in PA defaults
- **Assumption:** Metis and Griffin hardware specs match Hermes class-A 10 W radios
- **Correctness:** ✓ Matches Thetis setup.cs:482–544 (HERMES / HPSDR / ORIONMKII / ANAN10 bracket)

### Load-Bearing: Atlas / HermesII Not Reachable from Server
- P2 enum defines `Atlas` (0x00) and `HermesII` (0x02)
- Server code uses `Zeus.Protocol1.Discovery.HpsdrBoardKind` only
- If a P2 discovery returns board id 0x00 (Atlas), the mapping happens in P2 ReplyParser but the result cannot be passed to server-side dispatch
- **Resolution path needed:** Either unify enums or add protocol-aware dispatch layer

---

## Files Affected

### Enum Definitions
- `Zeus.Protocol1/Discovery/HpsdrBoardKind.cs` — P1 enum (Metis, Hermes, Griffin, Angelia, Orion, HL2, OrionMkII)
- `Zeus.Protocol2/Discovery/HpsdrBoardKind.cs` — P2 enum (Atlas, Hermes, HermesII, Angelia, Orion, HL2, OrionMkII)

### Discovery
- `Zeus.Protocol1/Discovery/ReplyParser.cs:119–129` — P1 board id → enum mapping
- `Zeus.Protocol2/Discovery/ReplyParser.cs:111–121` — P2 board id → enum mapping

### Server-Side Board Seams
- `Zeus.Server.Hosting/RadioDriveProfile.cs` — IRadioDriveProfile dispatch (HL2 vs. FullByte)
- `Zeus.Server.Hosting/PaDefaults.cs` — PA gain seeds + max-watts dispatch
- `Zeus.Server.Hosting/RadioCalibrations.cs` — Meter calibration dispatch + TODO(p2-cal)
- `Zeus.Contracts/RadioCalibration.cs` — Calibration bucket definitions
- `Zeus.Server.Hosting/RadioService.cs` — ConnectedBoardKind, EffectiveBoardKind, ResolvePsHwPeak, RecomputePaAndPush

### Wire Format
- `Zeus.Protocol1/ControlFrame.cs` — HL2-specific register encodings (DriveFilter, Attenuator, LnaTxGainStable, Predistortion, Config, IQ payload)

### Tests
- `tests/Zeus.Server.Tests/EffectiveBoardKindTests.cs` — Board kind resolution (P1 only)
- `tests/Zeus.Server.Tests/RadioCalibrationsDispatchTests.cs` — Calibration dispatch (G2 default for 0x0A)

---

## Recommendations for Next Phase

1. **Unify board enums** — decide on a server-side canonical enum that covers all P1 and P2 boards (Metis, Hermes, Griffin/HermesII, Angelia, Orion, HermesLite2, Atlas, OrionMkII)
2. **Add OrionMkII to PaDefaults.TableFor()** — use OrionG2Gains table (quick win)
3. **Discriminate ANAN-8000D** — extend discovery to expose firmware version or board-specific field; wire calibration dispatch to pick the correct bridge
4. **P2 board resolution** — resolve P2-discovered board ids (Atlas, HermesII) to server-side equivalents; update ConnectedBoardKind property to reflect actual board
5. **Test coverage** — add unit tests for P2-only board flows and ANAN-8000D discrimination logic
6. **PS P1 defer** — document why P1 PureSignal is marked TODO(ps-p1); confirm it's acceptable for HL2 (primary P1 PS user)


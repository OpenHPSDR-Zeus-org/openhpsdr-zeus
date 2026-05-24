# Thetis (MW0LGE) Board Support Matrix

Reference catalog of every HPSDR board that Richard Samphire's `ramdor/Thetis`
fork (MW0LGE) special-cases, extracted from `clsHardwareSpecific.cs`,
`enums.cs`, and `HPSDR/NetworkIO.cs` of the Thetis tree at
`/Users/bek/Data/Repo/github/Thetis/Project Files/Source/`.

Use this as the spec for extending Zeus's per-board seams
(`HpsdrBoardKind`, `RadioDriveProfile`, `PaDefaults`, `RadioCalibrations`,
the discovery `ReplyParser`s) to cover everything Thetis supports.

> Scope note: this catalog covers MW0LGE's `ramdor/Thetis` — the reference
> for ANAN-class radios. Hermes-Lite 2 behaviour is **not** sourced from
> here; HL2 has its own reference doc at
> `docs/references/protocol-1/hermes-lite2-protocol.md` and the
> mi0bot Thetis fork at `/Users/bek/Data/Repo/github/OpenHPSDR-Thetis`.

## Enums

### `HPSDRModel` (`enums.cs:109-132`)

The user-selected radio family. Implicit integer values (declaration
order). MW0LGE warns: keep order stable, append before `LAST`.

| # | Name | Notes |
|---|---|---|
| -1 | `FIRST` | sentinel |
| 0 | `HPSDR` | original Mercury/Penelope/Metis HPSDR ("G1") |
| 1 | `HERMES` | Hermes board (single-radio) |
| 2 | `ANAN10` | Apache Labs ANAN-10 |
| 3 | `ANAN10E` | Apache Labs ANAN-10E |
| 4 | `ANAN100` | Apache Labs ANAN-100 |
| 5 | `ANAN100B` | Apache Labs ANAN-100B |
| 6 | `ANAN100D` | Apache Labs ANAN-100D (dual-RX) |
| 7 | `ANAN200D` | Apache Labs ANAN-200D |
| 8 | `ORIONMKII` | Apache Labs Orion MkII |
| 9 | `ANAN7000D` | Apache Labs ANAN-7000DLE |
| 10 | `ANAN8000D` | Apache Labs ANAN-8000DLE |
| 11 | `ANAN_G2` | Apache Labs ANAN-G2 (Saturn FPGA) — G8NJJ |
| 12 | `ANAN_G2_1K` | Apache Labs ANAN-G2-1K (1 kW Saturn) — G8NJJ |
| 13 | `ANVELINAPRO3` | community board |
| 14 | `HERMESLITE` | Hermes-Lite — MI0BOT (out of scope here, Zeus has its own HL2 path) |
| 15 | `REDPITAYA` | Red Pitaya — DH1KLM |
| 16 | `ANAN_G2E` | Apache Labs ANAN-G2E — N1GP |
| 17 | `LAST` | sentinel |

### `HPSDRHW` (`enums.cs:389-402`) — the FPGA "board ID" reported in discovery

| Int | Name | Boards that report this ID |
|---|---|---|
| 0 | `Atlas` | Metis-class HPSDR motherboards |
| 1 | `Hermes` | Hermes / ANAN-10 / ANAN-100 |
| 2 | `HermesII` | ANAN-10E / ANAN-100B |
| 3 | `Angelia` | ANAN-100D |
| 4 | `Orion` | ANAN-200D |
| 5 | `OrionMKII` | Orion MkII / ANAN-7000DLE / ANAN-8000DLE / ANVELINA-PRO3 / Red Pitaya |
| 6 | `HermesLite` | Hermes-Lite / Hermes-Lite 2 |
| 10 | `Saturn` | ANAN-G2 / ANAN-G2-1K |
| 11 | `SaturnMKII` | reserved (G8NJJ comment: "MKII board?") |
| 20 | `HermesC10` | ANAN-G2E (N1GP) |
| 999 | `Unknown` | sentinel |

### Wire-byte → `HPSDRHW`

Active discovery code casts the raw byte directly: `(HPSDRHW)data[10]` for
Protocol 1 and `(HPSDRHW)data[11]` for Protocol 2 (`HPSDR/NetworkIO.cs:980`,
in the legacy commented block now superseded by `RadioDiscoveryService`).

The legacy switch around `HPSDR/NetworkIO.cs:999-1020` documents the P1
byte→enum mapping that is now implicit in the cast:

| P1 byte | → `HPSDRHW` |
|---|---|
| 0x00 | Atlas |
| 0x01 | Hermes |
| 0x02 | HermesII |
| 0x04 | Angelia |
| 0x05 | Orion |
| 0x06 | HermesLite |
| 0x0A (10) | OrionMKII / Saturn (collision — software disambiguates by sample-rate / FPGA fingerprint) |
| 0x14 (20) | HermesC10 |

The `0x0A` collision between OrionMKII and Saturn is called out by G8NJJ in
the source comment at `clsHardwareSpecific.cs:171` and again at
`HPSDR/NetworkIO.cs:1016`; it is a known wire-format ambiguity that requires
out-of-band disambiguation.

## Per-board behaviour matrix

### Initialisation side-effects (`clsHardwareSpecific.cs:85-192`)

Set when `HardwareSpecific.Model = ...` fires. These four calls are the
hardware-conditioning fingerprint of the radio:

| Model | RxADC | MKIIBPF | ADCSupply (mV) | LRAudioSwap | → `HPSDRHW` |
|---|---|---|---|---|---|
| `HERMES` | 1 | 0 | 33 | 1 | `Hermes` |
| `ANAN10` | 1 | 0 | 33 | 1 | `Hermes` |
| `ANAN10E` | 1 | 0 | 33 | 1 | `HermesII` |
| `ANAN100` | 1 | 0 | 33 | 1 | `Hermes` |
| `ANAN100B` | 1 | 0 | 33 | 1 | `HermesII` |
| `ANAN100D` | 2 | 0 | 33 | 0 | `Angelia` |
| `ANAN_G2E` | 1 | 1 | 33 | 0 | `HermesC10` |
| `ANAN200D` | 2 | 0 | 50 | 0 | `Orion` |
| `ORIONMKII` | 2 | 1 | 50 | 0 | `OrionMKII` |
| `ANAN7000D` | 2 | 1 | 50 | 0 | `OrionMKII` |
| `ANAN8000D` | 2 | 1 | 50 | 0 | `OrionMKII` |
| `ANAN_G2` | 2 | 1 | 50 | 0 | `Saturn` |
| `ANAN_G2_1K` | 2 | 1 | 50 | 0 | `Saturn` (G8NJJ: "likely to need further changes for PA") |
| `ANVELINAPRO3` | 2 | 1 | 50 | 0 | `OrionMKII` |
| `REDPITAYA` | 2 | 0 | 50 | 0 | `OrionMKII` (DH1KLM: BPF=0 for OpenHPSDR-compat DIY PA/Filter boards) |
| `HERMESLITE` | — | — | — | — | **no setter case** in MW0LGE Thetis |
| `HPSDR` (G1) | — | — | — | — | **no setter case** (default-handled elsewhere) |

Pattern summary:
- **Hermes family** (`HERMES`, `ANAN10`, `ANAN10E`, `ANAN100`, `ANAN100B`):
  single RX ADC, no MKII BPF, 33 mV ADC supply, **LR audio swap on**.
- **DDC / dual-ADC family** (`ANAN100D`, `ANAN200D`, `ORIONMKII`,
  `ANAN7000D`, `ANAN8000D`, `ANAN_G2*`, `ANVELINAPRO3`, `REDPITAYA`):
  two RX ADCs, LR swap off.
- **MKII BPF on** is the marker of "second-generation Apache" boards
  (Orion MkII, 7000/8000-DLE, G2, G2-1K, ANVELINA-PRO3, G2E).
  Red Pitaya turns it off intentionally for DIY PA boards.
- **50 mV ADC supply** is the marker of high-power boards from ANAN-200D
  onwards.
- **Hermes-Lite is unconfigured** in MW0LGE's setter — MW0LGE leaves HL2
  to the mi0bot fork.
- **HPSDR (original G1)** also has no explicit setter case — Thetis
  treats it as the default Hermes-family configuration via the fall-through
  in `HardwareSpecific.Hardware` (which is `HPSDRHW.Unknown` until set).

### Volts / Amps telemetry (`clsHardwareSpecific.cs:245-264`)

Boards reporting on-board voltage and current sensors:

| Model | HasVolts | HasAmps |
|---|---|---|
| `ANAN7000D` | ✓ | ✓ |
| `ANAN8000D` | ✓ | ✓ |
| `ANVELINAPRO3` | ✓ | ✓ |
| `ANAN_G2E` | ✓ | ✓ |
| `ANAN_G2` | ✓ | ✓ |
| `ANAN_G2_1K` | ✓ | ✓ |
| `REDPITAYA` | ✓ | ✓ |
| (all others) | ✗ | ✗ |

### Default volt-meter calibration (`clsHardwareSpecific.cs:265-292`)

`(voff, sens)` tuple per family:

| Models | voff | sens |
|---|---|---|
| `ANAN7000D`, `ANVELINAPRO3`, `REDPITAYA` | 340.0 | 88.0 |
| `ANAN_G2` | 0.001 | 66.23 |
| `ANAN_G2_1K` | 0.001 | 66.23 (G8NJJ note: "will need adjustment probably") |
| (default) | 360.0 | 120.0 |

### PureSignal default `hw_peak` (`clsHardwareSpecific.cs:295-321`)

Read on `HPSDRHW`, not `HPSDRModel`:

| Protocol | `HPSDRHW` | PSDefaultPeak |
|---|---|---|
| Protocol 1 (USB) | (any) | 0.4072 |
| Protocol 2 (ETH) | `Saturn` | 0.6121 |
| Protocol 2 (ETH) | (any other) | 0.2899 |

Zeus's HL2 default is 0.233 (lowered in tuning) and is **not** sourced from
this table — see `docs/lessons/hl2-ps-hwpeak-calibration.md`. The above
table applies to non-HL2 boards only.

### RX meter / display calibration offsets (`clsHardwareSpecific.cs:407-455`)

Default dB offsets applied to the front-end:

| Models | RXMeterCal | RXDisplayCal |
|---|---|---|
| `ANAN7000D`, `ANAN8000D`, `ORIONMKII`, `ANVELINAPRO3`, `REDPITAYA` | 4.841644 | 5.259 |
| `ANAN_G2`, `ANAN_G2_1K` | -4.476 | -4.4005 |
| (default — Hermes/ANAN-10/100/100D/200D/G2E) | 0.98 | -2.1 |

### Audio amplifier presence (Protocol 2 only) (`clsHardwareSpecific.cs:458-468`)

Models with on-board headphone/audio amplifier (Protocol 2 path only):

`ANAN7000D`, `ANAN8000D`, `ANVELINAPRO3`, `ANAN_G2`, `ANAN_G2_1K`, `REDPITAYA`.

### Default per-band PA gains (`clsHardwareSpecific.cs:471-769`)

These are PA *attenuations* in dB; 100.0 means "no output power". Stored
as `float[(int)Band.LAST]`. HF-only entries shown — VHF entries are
identical for all boards listed (`63.1` for the high-power Saturn-class
boards, `56.2` for everything else).

| Band | HERMES / HPSDR / ORIONMKII | ANAN10 / ANAN10E | ANAN100 / ANAN100B | ANAN100D / ANAN200D | ANAN8000D | 7000D / G2E / G2 / ANVELINAPRO3 / REDPITAYA | ANAN_G2_1K |
|---|---|---|---|---|---|---|---|
| 160 m | 41.0 | 41.0 | 50.0 | 49.5 | 50.0 | 47.9 | 47.9 |
| 80 m | 41.2 | 41.2 | 50.5 | 50.5 | 50.5 | 50.5 | 50.5 |
| 60 m | 41.3 | 41.3 | 50.5 | 50.5 | 50.5 | 50.8 | 50.8 |
| 40 m | 41.3 | 41.3 | 50.0 | 50.0 | 50.0 | 50.8 | 50.8 |
| 30 m | 41.0 | 41.0 | 49.5 | 49.0 | 49.5 | 50.9 | 50.9 |
| 20 m | 40.5 | 40.5 | 48.5 | 48.0 | 48.5 | 50.9 | 50.9 |
| 17 m | 39.9 | 39.9 | 48.0 | 47.0 | 48.0 | 50.5 | 50.5 |
| 15 m | 38.8 | 38.8 | 47.5 | 46.5 | 47.5 | 47.0 | 47.0 |
| 12 m | 38.8 | 38.8 | 46.5 | 46.0 | 46.5 | 47.9 | 47.9 |
| 10 m | 38.8 | 38.8 | 42.0 | 43.5 | 42.0 | 46.5 | 46.5 |
| 6 m | 38.8 | 38.8 | 43.0 | 43.0 | 43.0 | 44.6 | 44.6 |
| VHF | 56.2 | 56.2 | 56.2 | 56.2 | 56.2 | 63.1 | 63.1 |

`ANAN_G2_1K` shares the 7000D/G2 HF row but lives in its own switch case
because it may need PA-specific tuning (G8NJJ comment at line 732).

### Path Illustrator UI support (`clsHardwareSpecific.cs:773-780`)

`SupportsPathIllustrator` is `false` for: `ORIONMKII`, `ANAN7000D`,
`ANAN8000D`, `ANAN_G2`, `ANAN_G2_1K`, `ANVELINAPRO3`, `REDPITAYA`.
True for everything else (Hermes / ANAN-10 / ANAN-100 / ANAN-100D /
ANAN-200D / ANAN-G2E / HPSDR / Hermes-Lite).

### RX2 stepped attenuation (`clsHardwareSpecific.cs:783-803`)

RX1 is always stepped. RX2:
- **Firmware gain-reduction** (returns false): `HERMES`, `ANAN_G2E`,
  `ANAN10`, `ANAN10E`, `ANAN100`, `ANAN100B`.
- **Hardware stepped attenuator** (returns true): everything else
  (`ANAN100D`, `ANAN200D`, `ORIONMKII`, `ANAN7000D`, `ANAN8000D`,
  `ANAN_G2`, `ANAN_G2_1K`, `ANVELINAPRO3`, `REDPITAYA`,
  `HERMESLITE` falls in this default bucket but Zeus does not use this
  table for HL2).

### String ↔ enum (`clsHardwareSpecific.cs:325-404`)

User-facing names (`StringModelToEnum` / `EnumModelToString`):

| Enum | String |
|---|---|
| `HERMES` | "HERMES" |
| `ANAN10` | "ANAN-10" |
| `ANAN10E` | "ANAN-10E" |
| `ANAN100` | "ANAN-100" |
| `ANAN100B` | "ANAN-100B" |
| `ANAN100D` | "ANAN-100D" |
| `ANAN200D` | "ANAN-200D" |
| `ANAN7000D` | "ANAN-7000DLE" |
| `ANAN8000D` | "ANAN-8000DLE" |
| `ANAN_G2` | "ANAN-G2" |
| `ANAN_G2_1K` | "ANAN-G2-1K" |
| `ANVELINAPRO3` | "ANVELINA-PRO3" |
| `HERMESLITE` | "HERMESLITE" / "HERMES-LITE" |
| `REDPITAYA` | "RED-PITAYA" |
| `ANAN_G2E` | "ANAN-G2E" |
| (default for unknown input) | `HPSDRModel.HERMES` |

`HPSDRModel.HPSDR` (the original "G1" Mercury/Penelope/Metis) and `ORIONMKII`
have no entries in the string conversion table — Thetis's UI does not
let users pick them by name; they're only reachable via discovery /
loaded settings.

## Open questions / out of scope

- **Drive-byte resolution** is not switched on `HPSDRModel` in
  `clsHardwareSpecific.cs`. MW0LGE's drive math lives elsewhere — likely
  in a `Console.cs` `MOX` path or in the protocol writer. Zeus already
  has the seam at `Zeus.Server.Hosting/RadioDriveProfile.cs` and only HL2
  needs the 4-bit quantisation.
- **PureSignal feedback-source selector** (internal coupler vs external)
  is not in this file. Zeus must keep the HL2 internal-coupler radio
  exposed (per `feedback_hl2_has_internal_coupler.md`); for non-HL2 boards
  the selector behaviour follows the Apache Labs convention covered by
  Thetis's PS panel UI elsewhere.
- **HPSDR (G1) initialisation defaults** are not in the setter switch.
  When supporting "G1" in Zeus, the Hermes-family fingerprint
  (1 RX ADC / no MKII BPF / 33 mV / LR-swap-on) is the safe default,
  matching what the original HPSDR Mercury/Penelope/Metis stack expects.

## Cross-reference: Zeus seams (post-#218 status)

| Thetis surface | Zeus seam | Status |
|---|---|---|
| `HardwareSpecific.Hardware` setter side-effects | `Zeus.Contracts.HpsdrBoardKind` + `RadioService.ConnectedBoardKind` | ✅ unified across P1/P2 in Phase 4 (`932c040`); every documented wire byte parsed and dispatched |
| `DefaultPAGainsForBands` | `Zeus.Server.Hosting/PaDefaults.cs` | ✅ HermesGains / Anan100Gains / Anan200Gains / OrionG2Gains tables; variant-aware overload routes 0x0A sub-variants per Phase 3 |
| `RXMeterCalbrationOffsetDefaults` / `RXDisplayCalbrationOffsetDefauls` | `Zeus.Server.Hosting/RadioCalibrations.cs` (TX-side) | ✅ Hermes / Anan100 / Anan200 / OrionMkII / OrionMkIIAnan8000 / OrionMkIIOriginal / AnanG21K buckets |
| `GetDefaultVoltCalibration` | not yet wired in Zeus | ⏳ deferred; Zeus has no operator volts/amps panel today, so no consumer for the per-board `(voff, sens)` constants |
| `PSDefaultPeak` | `RadioService.ResolvePsHwPeak` | ✅ Phase 6 (`59456d1`); variant-aware (G2 / G2_1K → 0.6121, others → 0.2899); HL2 0.233 preserved |
| `HasVolts` / `HasAmps` / `HasAudioAmplifier` / `HasSteppedAttenuationRx2` / `SupportsPathIllustrator` | `Zeus.Contracts.BoardCapabilities` + `BoardCapabilitiesTable.For` | ✅ Phase 2 (`b0afe62`); fetched by frontend via `/api/radio/capabilities` (Phase 5) — UI panel gating is for future panels to consume |
| `HPSDRModel.ANAN_G2E` (HermesC10) | `HpsdrBoardKind.HermesC10` | ✅ recognised on discovery (`83364ec`) and dispatched (`1bcbd7d`) |
| 0x0A wire-byte alias family disambiguation | `Zeus.Contracts.OrionMkIIVariant` | ✅ Phase 3 (`d807611`); operator-selectable per-radio, persisted in `PreferredRadioStore`, surfaced via `/api/radio/variant` and the `RadioSelector` dropdown |

## 7000DLE MKIII vs MKII (bd zeus-pqp, #218 follow-up)

kw4ex on #218 asked whether the ANAN-7000DLE MKIII needs its own
`OrionMkIIVariant` bucket distinct from MKII. Audit result: **no — all
three reference codebases treat 7000DLE / 7000DLE MKII / (hypothetical)
MKIII as the same hardware**, dispatched through the OrionMKII bucket.

Refs (read 2026-05-24):

- **ramdor / MW0LGE Thetis** (`/Users/bek/Data/Repo/github/Thetis`, tip
  `3759d096` v2.10.3.15) —
  - `Project Files/Source/Console/enums.cs:396` — single comment lumps
    `AMAM-7000DLE 7000DLEMkII ANAN-8000DLE OrionMkII Anvelina-Pro3 RedPitaya`
    into `OrionMKII = 5`.
  - `clsHardwareSpecific.cs:343` — `"ANAN-7000DLE"` is the only model
    string; there is no `"ANAN-7000DLE MKIII"` / `"MK3"` arm in
    `StringToEnumModel` (the `case "ANAN-…"` block runs 330-360).
  - `console.cs:8568`, `console.cs:8653` — both PA / TX paths switch on
    `HPSDRHW.OrionMKII` with the same membership comment; no MKIII branch.
  - Repo-wide `grep -rni "mkiii\|mk3\|7000DLEMkIII"` returns zero hits
    against radio code (the only `MK3` hits in `display.cs` / `console.cs`
    are `MMK3`, the memory-spot count).
- **mi0bot / OpenHPSDR-Thetis** (HL2 fork,
  `/Users/bek/Data/Repo/github/OpenHPSDR-Thetis`) —
  - `enums.cs:393` carries the identical OrionMKII comment.
  - `clsHardwareSpecific.cs:351` matches MW0LGE's `"ANAN-7000DLE"` arm;
    no MKIII case.
- **dl1bz / deskhpsdr** (`/Users/bek/Data/Repo/github/deskhpsdr`) —
  - `src/discovered.h:33` — "ANAN 7000DLE and 8000DLE uses 10 as the
    device type in old protocol" (single device-type value, no MKIII
    sub-disambiguation).
  - `src/saturnregisters.c:728` — single "7000DLE RF board" IC map; no
    MKIII variant.
  - Repo-wide `grep -rni "mkiii\|mk3\|7000DLEMkIII"` returns zero hits.
- **piHPSDR** — not cloned locally on this machine (checked
  `/Users/bek/Data/Repo` exhaustively); deskhpsdr is a piHPSDR descendant
  and inherits the same device-type 10 model, so the piHPSDR codebase
  behaviour is covered transitively.

Conclusion: **MKIII == MKII at the protocol / DSP / calibration layer for
all three reference implementations.** Apache Labs hardware revisions of
the 7000DLE chassis do not change the protocol-1 wire fingerprint or PA
gain bracket that Thetis / deskhpsdr / piHPSDR care about. **Zeus needs
no `Anan7000DLE_MkIII` bucket.** Operators with a physical MKIII should
select `Anan7000DLE` and report any forward-power discrepancy on a known
dummy load — if a real difference shows up at the bench, the variant
seam in `Zeus.Contracts.OrionMkIIVariant` is the place to add it then,
not now on speculation.

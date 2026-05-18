# HL2 drive model — percentage-based, not dB

Load-bearing invariant for anyone touching `HermesLite2DriveProfile`, the
HL2 path in `PaDefaults.cs`, or the PA Settings panel. Reading this before
debugging "HL2 makes the wrong power" saves ~two days of chasing the
piHPSDR byte-math shadow.

## TL;DR

**HL2 is not driven like every other HPSDR radio.** Hermes / ANAN / Orion
use piHPSDR's / Thetis's 8-bit dB-based drive model:

    target_watts → target_dBm − PaGainDb → source_volts → byte(0..255)

HL2 does **not**. The mi0bot openhpsdr-thetis fork (the HL2-specific Thetis
upstream) drives HL2 via a **percentage-based** path where the same
`_gainValues` field that holds decibels for other radios is overloaded to
hold **output percentage (0..100)**. The drive byte is a direct
slider × percentage mapping, then nibble-quantised because HL2 gateware
reads only bits [31:28] of the drive-level register.

Because Zeus copied the piHPSDR dB math into `ComputeFullByte` and then
bolted nibble quantisation on top, `PaGainDb = 40.5 dB` (piHPSDR's
published default) produced byte = 48 → nibble 0x3 → **20 % of rated
output, no matter what HL2 is on the bench**. The only way to get rated
output was to lower `PaGainDb` until the dB-math happened to produce
byte ≥ 240 — calibration floor around 26 dB, per the byte-math.

The real fix, landed on this branch, is to abandon the dB model on HL2
and mirror mi0bot's percentage model directly. After this fix:
`PaGainDb = 100` (output %) on HF bands → byte = 240 → nibble 0xF → rated.

## The symptom

- Zeus TX power topping out at 1–2 W on 20 m where the same HL2 +
  antenna produces 5–7 W on deskHPSDR / piHPSDR.
- Measured power scales as roughly `byte^0.86` against `driveByte` — not
  the `byte^2` a class-AB amplifier should produce. Evidence it's
  quantisation, not hardware compression.
- `p1.tx.rate` log (in `Protocol1Client.TxLoopAsync`) shows `drv=48` even
  when `pa.recompute` reports `byte=48` as "correct" per the old
  `ComputeDriveByte` formula with `gainDb=40.5, maxWatts=5, pct=100`.

## The cause (what's actually on the wire)

From `docs/references/protocol-1/hermes-lite2-protocol.md:51`:

    | 0x09 | [31:24] | Hermes TX Drive Level (only [31:28] used)

Every HPSDR radio speaks this same register; only HL2's gateware throws
away the bottom nibble. But **what the driver puts into the top nibble**
is where the two models diverge:

### What piHPSDR / Thetis (for Hermes / ANAN / Orion) does

`radio.c:2809-2828` in piHPSDR:

    target_watts = max_watts * slider_pct / 100
    source_watts = target_watts / 10^(pa_gain_dB / 10)
    source_volts = sqrt(source_watts * 50)                 # 50 Ω load
    norm         = source_volts / 0.8                      # 0..1
    byte         = round(norm * 255)                        # 0..255

At slider=100 %, pa_gain=40.5 dB, max_watts=5: byte = 48. For a full-byte
radio (Hermes/ANAN/Orion) this produces 5 W because those boards use the
full 8-bit register. For HL2 the gateware only looks at 48 >> 4 = 0x3,
so output caps at 3/15 × rated = ~1 W.

### What mi0bot's HL2-specific Thetis fork actually does

`clsHardwareSpecific.cs:767-795` — the per-band `_gainValues` for HL2 is
a **percentage**:

    HF bands (160m..10m):  100    // 100 % of max, no attenuation
    6 m:                    38.8  // stock HL2 PA is weaker at 50 MHz

The wire path (`console.cs:49290-49299` + `audio.cs:249-258` in that fork):

    // (at line 49296, inside the HL2 branch)
    RadioVolume = min( (hl2Power × (gbb / 100)) / 93.75 , 1.0 )

    // then the setter at audio.cs:256
    NetworkIO.SetOutputPower( RadioVolume × 1.02 )   // native → wire byte 0..255

Where `hl2Power = new_pwr` (slider 0..90 in mi0bot — it deliberately caps
its slider at 90 so six-step moves land on nibble boundaries) and
`gbb = GainByBand(band, new_pwr)` is the percentage above.

The constants `93.75` and `1.02` are calibration:
`1/((16/6)/(255/1.02)) = 93.75`. Translation: mi0bot's slider has 15
steps of 6 (0, 6, 12, …, 90), each of which should land the wire byte on
the next nibble boundary. 1.02 is the overshoot factor so `SetOutputPower`
saturates at byte 250 → nibble 0xF at slider=90.

**So in mi0bot, `_gainValues` is a per-band output-% soft-cap.** The
math is a two-factor product (slider × band-pct) then scale to the
byte space. There is no target-watts, no source-volts, no 0.8 V
reference. Those are piHPSDR-isms that don't apply to HL2's gateware.

## What Zeus now does

`Zeus.Server.Hosting/RadioDriveProfile.cs — HermesLite2DriveProfile.EncodeDriveByte`:

    byte_raw = round( (drivePct/100) × (paGainDb/100) × 255 )
    byte     = round( byte_raw / 16 ) × 16            // nibble-quantise

Reading the parameter `paGainDb` as a percentage on HL2 (the DTO field
stays named that across boards; semantics are resolved per-profile).
`maxWatts` is ignored on HL2 — no target-watts formula.

At slider=100, paGainDb=100 (HF default): byte_raw = 255 → byte = 240 →
nibble 0xF → **rated output** on first connect, no operator calibration
required.

At slider=100, paGainDb=38.8 (6 m default): byte_raw = 99 → byte = 96 →
nibble 0x6 → **40 % of rated**, mi0bot's soft-cap for stock-PA 6 m.

### Worked example (what used to be broken)

Old dB model at `PaGainDb=40.5, maxWatts=5, pct=100`:

    target  = 5 W
    source  = 5 / 10^(40.5/10) = 4.46e-4 W
    volts   = sqrt(4.46e-4 × 50) = 0.1493 V
    norm    = 0.1493 / 0.8 = 0.187
    byte    = round(0.187 × 255) = 48        = 0x30   = 0b00110000
                                                 ^^^^
                               upper nibble = 0x3 = 3 of 15 (20 %)

The byte *looked* sensible on a 0–255 scale. The HL2 only read the 3.

New percentage model at `PaGainDb=100, pct=100`:

    norm = 1.00 × 1.00 = 1.00
    byte = round(1.00 × 255) = 255, nibble-quantised → 240
    nibble = 0xF = 15 of 15 = rated output ✓

## How to recognise the old (broken) behaviour

Key TUN, watch `p1.tx.rate` at 1 Hz. It prints the byte just sent:

    p1.tx.rate ... drv=240    ← upper nibble 0xF = 100 %, good
    p1.tx.rate ... drv=48     ← upper nibble 0x3 = 20 %, OLD MODEL
    p1.tx.rate ... drv=96     ← upper nibble 0x6 = 40 %, 6 m soft-cap
                                   or non-full slider position

Rule of thumb: `nibble = drv >> 4`. Power ≈ `(nibble / 15) × rated`.

## The operator-facing UI

On HL2, the "PA Gain (dB)" field is relabelled **"PA Output (%)"** with
range 0..100, step 1. Non-HL2 radios keep "PA Gain (dB)" with range 0..70,
step 0.1. The DTO field name stays `paGainDb` for storage compatibility
across boards — the label change is pure frontend UX.

Operators who had hand-calibrated values from the old dB era will see
those values silently reinterpreted as percentages on first load
(M3 "just ship it" migration). Press **Reset to Hermes Lite 2 defaults**
in the PA Settings panel to seed the mi0bot defaults (100 % HF / 38.8 %
6 m). A stored `40.5` now means "40.5 % output" (~nibble 0x6, 40 %) —
worse than the 100 % default. Stored `26` (the old workaround) now means
"26 % output" (~nibble 0x4, 27 %) — also worse. Either way: hit Reset.

## When adding a new board

If the new board is Hermes / ANAN / Orion family with 8-bit drive: it
falls through to `FullByteDriveProfile` and uses the piHPSDR dB model.
No code needed beyond any custom quirks.

If the new board has a gateware quirk like HL2's nibble truncation, a
non-linear drive register, or a non-dB calibration convention:

1. Implement `IRadioDriveProfile` with the board-specific math.
2. Extend `RadioDriveProfiles.For(...)` to dispatch on it.
3. Update `PaDefaults.GetPaGainDb(...)` with the appropriate seed
   values (and document which unit — dB or % — they're in).
4. If the semantics differ from dB, add a frontend branch in
   `PaSettingsPanel.tsx` to relabel / reclamp the field when that board
   is selected, matching the HL2 branch.

**Do NOT reintroduce per-board branching inside `RadioService` or
`ComputeDriveByte`.** That's exactly the shape the HL2 bug had before
the profile abstraction landed, and before this percentage-model fix.

## References

- `docs/references/protocol-1/hermes-lite2-protocol.md:51` — the one
  line that would have saved two days.
- `Zeus.Server.Hosting/RadioDriveProfile.cs` — `HermesLite2DriveProfile`.
- `Zeus.Server.Hosting/PaDefaults.cs` — `Hl2OutputPct` table (HF 100 / 6 m 38.8).
- `Zeus.Protocol1/ControlFrame.cs` — `LastPeakAbs` / `LastDriveByte`
  instrumentation; `Protocol1Client.TxLoopAsync`'s `p1.tx.rate` log
  surfaces them at 1 Hz.
- `../OpenHPSDR-Thetis/Project Files/Source/Console/clsHardwareSpecific.cs:767-795`
  — mi0bot's HL2 default percentages.
- `../OpenHPSDR-Thetis/Project Files/Source/Console/console.cs:49290-49299`
  — mi0bot's HL2 RadioVolume formula.
- `../OpenHPSDR-Thetis/Project Files/Source/Console/audio.cs:249-258`
  — `Audio.RadioVolume` setter → `NetworkIO.SetOutputPower` → wire byte.

## Debugging heuristic

When HL2 output doesn't match a reference client, **read the board-
specific mi0bot fork before assuming the generic Thetis / piHPSDR code
applies**. HL2 has enough gateware quirks that the generic sources are
wrong in the detail that matters most: how bytes on the wire map to
output power. Two days spent on the piHPSDR dB-math shadow answered
that lesson on entry to this branch.

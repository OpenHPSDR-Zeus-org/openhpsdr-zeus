# Hermes / Brick2 / ANAN-10(E) — `MaxPowerWatts` must match the gain-bracket assumption, not the rated output

Load-bearing invariant for anyone touching `PaDefaults.GetMaxPowerWatts`,
the `HermesGains` table, or the drive-byte math in
`RadioDriveProfile.cs:DriveByteMath.ComputeFullByte`. Misreading this
makes a Brick2 (or any Hermes-class 10 W radio) put out **~1 W at max
TUN** while every meter in Zeus says "100 %".

## TL;DR

Thetis's drive slider is **0..100 watts** (a target output in real
watts). Zeus's slider is **0..100 percent of `MaxPowerWatts`**. The
`HermesGains` PA-gain bracket (38.8 dB on 10 m) was lifted from Thetis
`setup.cs:482-544`, which calibrates that gain assuming a **100 W
target**. Drive byte = 255 then corresponds to 100 W at the antenna —
the physical radio just self-clamps to whatever it can actually deliver.

If you set Zeus's `MaxPowerWatts = 10` for a Hermes-class board (the
rated output of an ANAN-10 / ANAN-10E / Brick2), the percent-of-max
math asks the DAC for **10 W** at slider=100, which through 38.8 dB of
PA gain is a 1.3 mW (-29 dBm) DAC ask → byte ≈ 82 → ~32 % of full DAC
amplitude → ~10 % of full power → **~1 W** out of a radio rated for 10 W.

The fix is `MaxPowerWatts = 100` for Hermes / HermesII / Metis. The
radio's rated max is **not** what this field means — it's the calibrated
output that pairs with the gain bracket so byte=255 at slider=100.

## The symptom

- Brick2 / ANAN-10E / ANAN-10 puts out ~1 W at max TUN on every HF band.
- Drive slider at 100 % during MOX produces the same ~1 W.
- `pa.recompute` log in `RadioService.RecomputePaAndPush` shows
  `byte=82 pct=100 gainDb=38.8 maxW=10` on 10 m — math is consistent,
  it's the inputs that are wrong.

## Why the two clients disagree on slider semantics

In Thetis (`console.cs:46724` `SetPowerUsingTargetDBM`):

    new_pwr      = slider.Value                   // 0..100 *WATTS*
    target_dbm   = 10 * log10(new_pwr * 1000)
    target_dbm  -= GainByBand(band, new_pwr)
    target_volts = sqrt(10^(target_dbm/10) * 0.05)
    RadioVolume  = min(target_volts / 0.8, 1.0)
    // NetworkIO.SetOutputPower(RadioVolume * 1.02)
    //   → wire byte = (int)(255 * value * swr_protect)

Slider=100 → 100 W target → byte=255 regardless of whether the radio is
physically 10 W or 100 W. A 10 W radio just hits its own ceiling.

In Zeus (`RadioDriveProfile.cs:DriveByteMath.ComputeFullByte`):

    targetWatts = MaxPowerWatts * drivePct / 100
    sourceWatts = targetWatts / 10^(paGainDb/10)
    sourceVolts = sqrt(sourceWatts * 50)
    norm        = clamp(sourceVolts / 0.8, 0, 1)
    driveByte   = round(norm * 255)

If `MaxPowerWatts = 10` and the gain bracket assumes 100 W at byte=255,
the operator can never reach byte=255 — the math caps at 1/10 the
amplitude (≈ byte 82, which is sqrt(1/10) × 255 ≈ 80).

## Rule for new boards

When seeding `PaDefaults.GetMaxPowerWatts`:

- **`MaxPowerWatts` is the target watts at which the chosen gain bracket
  reaches byte=255.** Set it to whatever the Thetis bracket you copied
  from was calibrated against (typically 100 for HF, 200 for 8000DLE,
  1000 for G2-1K). Do **not** set it to the physical radio's rated
  output unless the gain bracket was also calibrated for that output.
- HL2 is the exception — its `IRadioDriveProfile` does not consult
  `MaxPowerWatts` at all, so the field is cosmetic on that board and
  the rated 5 W is fine (see `hl2-drive-model.md`).

## Verifying a new board

1. Pick a band — 10 m is convenient because most boards have a
   `HermesGains["10m"]`-like 38–47 dB seed there.
2. Compute the byte the math produces at `drivePct=100`. With
   `MaxPowerWatts=100`, `paGainDb=38.8`, `drivePct=100`:

        targetWatts = 100
        sourceWatts = 100 / 7586 = 0.01318
        sourceVolts = sqrt(0.01318 * 50) = 0.812
        norm        = min(0.812/0.8, 1) = 1.0
        driveByte   = 255  ✓

3. On the wire, the `p1.tx.rate` log line (`Protocol1Client.TxLoopAsync`)
   reports `drv=255` at the EP2 packer when MOX/TUN is keyed at max
   drive. If `drv` saturates well below 255 at 100 % slider, the
   `MaxPowerWatts` / `PaGainDb` pair don't match the gain bracket — fix
   one before recalibrating per-band gains.

## References

- Thetis `console.cs:46724-46842` — `SetPowerUsingTargetDBM` (slider-watts → bytes).
- Thetis `audio.cs:262-271` — `RadioVolume` setter → `NetworkIO.SetOutputPower`.
- Thetis `HPSDR/NetworkIO.cs:199-212` — drive-byte serialisation.
- Thetis `setup.cs:482-544` — HERMES / HPSDR / ORIONMKII / ANAN10 /
  ANAN10E PA-gain bracket (the source of `HermesGains`).
- `RadioDriveProfile.cs` (Zeus) — `DriveByteMath.ComputeFullByte`.
- `PaDefaults.cs` (Zeus) — `GetMaxPowerWatts` / `HermesGains`.
- `hl2-drive-model.md` — the analogous lesson for HL2's percentage model.

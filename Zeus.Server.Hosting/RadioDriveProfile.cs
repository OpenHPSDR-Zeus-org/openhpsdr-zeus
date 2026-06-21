// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Per-radio quirks in how the drive-level byte actually makes it from
// Zeus to RF. Every HPSDR radio ostensibly speaks the same protocol 1 /
// protocol 2 wire format, but what each one DOES with the byte we put
// in C0=0x12, C1=drive_level varies:
//
//   - Hermes / ANAN-10 / ANAN-100 / Orion / G2  — use all 8 bits.
//     Fine-grained drive control; the classic Thetis/piHPSDR math
//     (target watts → dBm − PA gain → volts @ 50 Ω → byte/255 × 0.8 V)
//     maps cleanly to output power.
//
//   - Hermes-Lite 2 — reads ONLY bits [31:28] of the drive register.
//     The bottom nibble is silently discarded by the HL2 gateware.
//     That means the 8-bit byte the math produces is quantised to
//     one of 16 power steps. A "correct-looking" byte like 48
//     (from piHPSDR's generic pa_calibration=40.5 with 5 W rated PA)
//     lands in nibble 0x3 → 3/15 = 20 % of max drive, capping
//     output at 1–2 W no matter how precise the IQ or how correct
//     the packet rate. See docs/references/protocol-1/
//     hermes-lite2-protocol.md:51 and docs/lessons/
//     hl2-drive-byte-quantization.md.
//
// IMPORTANT for anyone touching TX / PA / drive-byte code:
//
//   Go through this abstraction. Do not hard-code an 8-bit linear
//   voltage model and expect it to "just work" on every board — it
//   won't on HL2, and the silence on the bench can eat a day before
//   you realise the bytes you're computing aren't the bytes the
//   radio is honouring. Add new board quirks by implementing
//   IRadioDriveProfile and extending RadioDriveProfiles.For.
//
// Reference implementations:
//   - piHPSDR radio.c:2809-2828 (8-bit, no HL2 quantisation — HL2
//     users happen to land drive high enough that nibble 0xF is
//     reached, which is why their "it works" setups work).
//   - Thetis console.cs:46801-46841 (8-bit, no HL2-specific branch).
//   - mi0bot/openhpsdr-thetis — HL2-specific fork; look there for
//     further HL2 quirks Zeus may need to mirror.

using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Server;

/// <summary>
/// Encapsulates per-board drive-byte encoding. Implementations convert a
/// calibrated drive % / PA-gain / max-watts triple into the final byte that
/// will be written to C0=0x12, C1 on the wire.
/// </summary>
public interface IRadioDriveProfile
{
    /// <summary>
    /// Board this profile targets. Diagnostic only.
    /// </summary>
    string BoardLabel { get; }

    /// <summary>
    /// Produce the byte to send in the DriveFilter C1 slot.
    /// </summary>
    /// <param name="drivePct">Operator slider position, 0..100.</param>
    /// <param name="paGainDb">Per-band PA calibration value from PaSettingsStore.
    /// <b>Interpretation depends on the profile:</b>
    /// <see cref="FullByteDriveProfile"/> reads it as dB forward gain
    /// (Hermes / ANAN / Orion convention). <see cref="HermesLite2DriveProfile"/>
    /// reads it as a per-band output percentage (0..100), matching mi0bot
    /// Thetis — see that class's comment for the full derivation.
    /// The DTO field name is retained for storage compatibility across
    /// boards, not because the semantics are uniform.</param>
    /// <param name="maxWatts">Rated PA output watts. 0 triggers the legacy
    /// straight-percent-to-byte mapping that pre-dates the PA math on
    /// FullByte profiles. Ignored on HL2 — percentage-based math doesn't
    /// consult rated watts.</param>
    byte EncodeDriveByte(int drivePct, double paGainDb, int maxWatts);
}

/// <summary>
/// Shared watts → drive-byte math used by every profile as the baseline.
/// Pure function, deterministic, unit-tested. Operator-facing calibration
/// lives in PaSettingsStore; this does not touch storage.
///
/// Reference: Thetis <c>console.cs:46801-46841</c>, piHPSDR
/// <c>radio.c:2809-2828</c>.
/// </summary>
internal static class DriveByteMath
{
    public static byte ComputeFullByte(int drivePct, double paGainDb, int maxWatts)
    {
        drivePct = Math.Clamp(drivePct, 0, 100);
        if (maxWatts <= 0)
        {
            return (byte)(drivePct * 255 / 100);
        }

        double targetWatts = maxWatts * drivePct / 100.0;
        if (targetWatts <= 0) return 0;

        double sourceWatts = targetWatts / Math.Pow(10.0, paGainDb / 10.0);
        double sourceVolts = Math.Sqrt(sourceWatts * 50.0);
        double norm = Math.Clamp(sourceVolts / 0.8, 0.0, 1.0);
        return (byte)Math.Round(norm * 255.0);
    }
}

/// <summary>
/// Default 8-bit profile for Hermes, ANAN-10/100/100D/200D/8000D, Orion,
/// Orion MkII (G1/G2/G2-1K) and anything else that honours the full drive
/// byte. No quantisation — the computed byte goes straight to the wire.
/// </summary>
public sealed class FullByteDriveProfile : IRadioDriveProfile
{
    public static readonly FullByteDriveProfile Instance = new();
    private FullByteDriveProfile() { }

    public string BoardLabel => "FullByte (8-bit)";

    public byte EncodeDriveByte(int drivePct, double paGainDb, int maxWatts)
        => DriveByteMath.ComputeFullByte(drivePct, paGainDb, maxWatts);
}

/// <summary>
/// Hermes-Lite 2 profile. HL2 is NOT driven by the piHPSDR/Thetis dB model
/// that every other HPSDR radio uses — it has a completely separate wire-
/// level power model that the mi0bot openhpsdr-thetis fork (the HL2-specific
/// Thetis upstream) implements in clsHardwareSpecific.cs:767-795 and
/// console.cs:49290-49299.
///
/// Semantics on HL2:
///   • <paramref name="paGainDb"/> is a <b>PER-BAND OUTPUT PERCENTAGE</b>
///     (0.0–100.0), not a dB forward gain. 100 = no attenuation;
///     for a weaker band (6 m on the stock HL2 PA) it's around 38.8.
///     The DTO field is still called <c>PaGainDb</c> for storage
///     compatibility with other boards — it's overloaded per board.
///   • <paramref name="maxWatts"/> is ignored. HL2 power is governed by
///     slider × band-percentage directly, not by a target-watts formula.
///
/// Math:
///     byte_raw = round( (drivePct / 100) × (paGainDb / 100) × 255 )
///     byte     = nearest-nibble( byte_raw )            // HL2 gateware quirk
///
/// Derivation from mi0bot Thetis (console.cs:49296, audio.cs:249-258):
///     RadioVolume        = slider × pctBand / 100 / 93.75     // 0..0.96
///     SetOutputPower arg = RadioVolume × 1.02
///     wire_byte          = SetOutputPower_arg × 255
/// The 1/((16/6)/(255/1.02)) = 93.75 constant is calibration for the
/// mi0bot 0–90 slider span. Zeus slides 0–100, so at drivePct=100 and
/// paGainDb=100 the raw byte reaches 255 and cleanly lands in nibble 0xF.
///
/// Reference:
///   • docs/references/protocol-1/hermes-lite2-protocol.md:51
///   • docs/lessons/hl2-drive-model.md
///   • ../OpenHPSDR-Thetis/Project Files/Source/Console/clsHardwareSpecific.cs:767-795
///   • ../OpenHPSDR-Thetis/Project Files/Source/Console/console.cs:49290-49299
/// </summary>
public sealed class HermesLite2DriveProfile : IRadioDriveProfile
{
    public static readonly HermesLite2DriveProfile Instance = new();
    private HermesLite2DriveProfile() { }

    public string BoardLabel => "HermesLite2 (%-scale, 4-bit)";

    public byte EncodeDriveByte(int drivePct, double paGainDb, int maxWatts)
    {
        // On HL2 "paGainDb" is a percentage, not decibels (see class-level
        // comment). Clamp to the percentage domain; maxWatts is ignored
        // because the HL2 drive pipeline is slider × band-percentage, no
        // target-watts conversion.
        _ = maxWatts;
        int pct = Math.Clamp(drivePct, 0, 100);
        double bandPct = Math.Clamp(paGainDb, 0.0, 100.0);
        double driveNorm = (pct / 100.0) * (bandPct / 100.0);
        byte raw = (byte)Math.Round(driveNorm * 255.0);

        // HL2 gateware reads only bits [31:28] of the drive register — the
        // bottom nibble is silently discarded. Round to the nearest 16-count
        // step so slider motion is honest (each step crosses one real power
        // level). Saturate at 15 so we never overflow.
        int nibble = (int)Math.Round(raw / 16.0);
        if (nibble > 15) nibble = 15;
        return (byte)(nibble * 16);
    }
}

/// <summary>
/// Per-board dispatch. Extend this switch whenever a new board needs a
/// non-default drive encoding. Anything not explicitly mapped falls through
/// to <see cref="FullByteDriveProfile"/>, which is the correct choice for
/// every full-Hermes-class radio.
/// </summary>
public static class RadioDriveProfiles
{
    public static IRadioDriveProfile For(HpsdrBoardKind board) => board switch
    {
        HpsdrBoardKind.HermesLite2 => HermesLite2DriveProfile.Instance,
        _                          => FullByteDriveProfile.Instance,
    };
}

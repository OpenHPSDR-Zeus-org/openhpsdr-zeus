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

using LiteDB;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Server;

// Persists the operator's chosen "this is my radio" preference. Fed into
// RadioService.EffectiveBoardKind so PA defaults, per-band gain tables and
// the PA Settings preview work before the radio is physically connected —
// otherwise the UI has to wait until after a successful connect to seed
// anything useful. Null here = "Auto" (let discovery pick the board).
//
// Lives in zeus-prefs.db alongside the other non-sensitive preferences.
//
// **Board Override Behavior:**
// When OverrideDetection is false (default): preference is for *configuration
// seeds only*, not for physics. Drive-byte encoding uses ConnectedBoardKind
// (what's on the wire). If an operator selects HL2 while plugged into a G2,
// we still use G2's 8-bit drive math on the wire.
//
// When OverrideDetection is true: preference overrides ConnectedBoardKind for
// ALL board-specific behavior including drive-byte encoding, ATT behavior, and
// filter switching. Use this for hardware combinations that report the wrong
// board ID or need different behavior than auto-detection provides (e.g.,
// Anvelina SDR + ANAN 200D PA detected as OrionMkII but needs Orion behavior).
// **CAUTION**: Setting the wrong board can result in incorrect drive levels,
// no output power, or hardware damage. Only use if you understand your hardware.
public sealed class PreferredRadioStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<PreferredRadioEntry> _entries;
    private readonly ILogger<PreferredRadioStore> _log;
    private readonly object _sync = new();

    public event Action? Changed;

    public PreferredRadioStore(ILogger<PreferredRadioStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _entries = _db.GetCollection<PreferredRadioEntry>("preferred_radio");

        _log.LogInformation("PreferredRadioStore initialized at {Path}", dbPath);
    }

    // null = "Auto" — no operator override, effective board tracks Connected.
    public HpsdrBoardKind? Get()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            if (e is null) return null;
            // A stored "Unknown" is indistinguishable from "Auto" and would
            // just seed junk PA defaults; treat it as Auto.
            return e.Board == HpsdrBoardKind.Unknown ? null : e.Board;
        }
    }

    // Returns whether the operator has explicitly enabled board override mode.
    // When true, the preferred board overrides ConnectedBoardKind for ALL
    // board-specific behavior. Default is false (safe mode).
    public bool GetOverrideDetection()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            return e?.OverrideDetection ?? false;
        }
    }

    public void Set(HpsdrBoardKind? board, bool? overrideDetection = null)
    {
        lock (_sync)
        {
            // "Auto" = store Unknown in the board slot so Get() returns null
            // without dropping sibling preferences that share this row
            // (variant, HL2 toggles, G2 ADC options, frequency calibration).
            if (board is null || board == HpsdrBoardKind.Unknown)
            {
                var existing = _entries.FindAll().FirstOrDefault();
                if (existing is null)
                {
                    _entries.Insert(new PreferredRadioEntry
                    {
                        Board = HpsdrBoardKind.Unknown,
                        OverrideDetection = false,
                        UpdatedUtc = DateTime.UtcNow,
                    });
                }
                else
                {
                    existing.Board = HpsdrBoardKind.Unknown;
                    if (overrideDetection.HasValue)
                    {
                        existing.OverrideDetection = overrideDetection.Value;
                    }
                    else
                    {
                        existing.OverrideDetection = false;
                    }
                    existing.UpdatedUtc = DateTime.UtcNow;
                    _entries.Update(existing);
                }
            }
            else
            {
                var existing = _entries.FindAll().FirstOrDefault();
                if (existing is null)
                {
                    _entries.Insert(new PreferredRadioEntry
                    {
                        Board = board.Value,
                        OverrideDetection = overrideDetection ?? false,
                        UpdatedUtc = DateTime.UtcNow,
                    });
                }
                else
                {
                    existing.Board = board.Value;
                    if (overrideDetection.HasValue)
                    {
                        existing.OverrideDetection = overrideDetection.Value;
                    }
                    existing.UpdatedUtc = DateTime.UtcNow;
                    _entries.Update(existing);
                }
            }
        }
        Changed?.Invoke();
    }

    public void SetOverrideDetection(bool enabled)
    {
        lock (_sync)
        {
            var existing = _entries.FindAll().FirstOrDefault();
            if (existing is not null)
            {
                existing.OverrideDetection = enabled;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
                Changed?.Invoke();
            }
        }
    }

    /// <summary>
    /// Returns the operator-selected variant for the 0x0A wire byte
    /// (issue #218). When no entry exists or the field is unset, returns
    /// <see cref="OrionMkIIVariant.G2"/> — Zeus' shipping default.
    /// Consulted only when the connected board kind is
    /// <see cref="HpsdrBoardKind.OrionMkII"/>; ignored otherwise.
    /// </summary>
    public OrionMkIIVariant GetOrionMkIIVariant()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            return e?.OrionMkIIVariant ?? OrionMkIIVariant.G2;
        }
    }

    /// <summary>
    /// Persists the operator's chosen variant. Stored in the same single-row
    /// preferences entry as the board / override-detection fields. Setting
    /// to <see cref="OrionMkIIVariant.G2"/> is identical to "unset" (the
    /// shipping default).
    /// </summary>
    public void SetOrionMkIIVariant(OrionMkIIVariant variant)
    {
        lock (_sync)
        {
            var existing = _entries.FindAll().FirstOrDefault();
            if (existing is null)
            {
                _entries.Insert(new PreferredRadioEntry
                {
                    Board = HpsdrBoardKind.Unknown,
                    OverrideDetection = false,
                    OrionMkIIVariant = variant,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.OrionMkIIVariant = variant;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// HL2 Band Volts PWM enable (issue #279). C3 bit 3 of the Protocol-1
    /// Config frame — same bit legacy boards used for LT2208 DITHER (which
    /// HL2's AD9866 doesn't need). When true, HL2 emits per-band-tagged PWM
    /// voltage on the FAN connector so an external amp (Xiegu XPA125B etc.)
    /// can auto-band-switch. Defaults to <c>false</c>; consulted only on HL2.
    /// </summary>
    public bool GetEnableHl2BandVolts()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            return e?.EnableHl2BandVolts ?? false;
        }
    }

    /// <summary>
    /// Persists the operator's chosen Band Volts PWM enable. Stored in the
    /// same single-row preferences entry alongside board / variant; setting
    /// to <c>false</c> is identical to "unset" (the shipping default).
    /// </summary>
    public void SetEnableHl2BandVolts(bool enabled)
    {
        lock (_sync)
        {
            var existing = _entries.FindAll().FirstOrDefault();
            if (existing is null)
            {
                _entries.Insert(new PreferredRadioEntry
                {
                    Board = HpsdrBoardKind.Unknown,
                    OverrideDetection = false,
                    EnableHl2BandVolts = enabled,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.EnableHl2BandVolts = enabled;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// ANAN-G2/Saturn ADC dither enable. Defaults to <c>true</c>, matching
    /// the Thetis G2 option block. Nullable storage lets older LiteDB rows
    /// that pre-date the field inherit the default-on behaviour.
    /// </summary>
    public bool GetG2AdcDitherEnabled()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            return e?.G2AdcDitherEnabled ?? true;
        }
    }

    /// <summary>
    /// ANAN-G2/Saturn ADC digital-output randomizer. Defaults to
    /// <c>true</c>, matching Thetis' Random Enabled default.
    /// </summary>
    public bool GetG2AdcRandomEnabled()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            return e?.G2AdcRandomEnabled ?? true;
        }
    }

    public int GetG2Rx1AttenuatorDb()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            return Math.Clamp(e?.G2Rx1AttenuatorDb ?? 0, 0, 31);
        }
    }

    public void SetG2AdcOptions(
        bool? ditherEnabled = null,
        bool? randomEnabled = null,
        int? rx1AttenuatorDb = null)
    {
        lock (_sync)
        {
            var existing = _entries.FindAll().FirstOrDefault();
            int? clampedRx1Atten = rx1AttenuatorDb.HasValue
                ? Math.Clamp(rx1AttenuatorDb.Value, 0, 31)
                : null;
            if (existing is null)
            {
                _entries.Insert(new PreferredRadioEntry
                {
                    Board = HpsdrBoardKind.Unknown,
                    OverrideDetection = false,
                    G2AdcDitherEnabled = ditherEnabled ?? true,
                    G2AdcRandomEnabled = randomEnabled ?? true,
                    G2Rx1AttenuatorDb = clampedRx1Atten ?? 0,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                if (ditherEnabled.HasValue)
                    existing.G2AdcDitherEnabled = ditherEnabled.Value;
                if (randomEnabled.HasValue)
                    existing.G2AdcRandomEnabled = randomEnabled.Value;
                if (clampedRx1Atten.HasValue)
                    existing.G2Rx1AttenuatorDb = clampedRx1Atten.Value;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Per-radio frequency-correction factor for crystal/clock drift
    /// (issue #325). Dimensionless multiplier near 1.0 applied host-side
    /// at the Protocol-1 and Protocol-2 SetVfoAHz seams before the value
    /// reaches the radio's NCO. 1.0 = uncalibrated (factory default).
    ///
    /// Models the Thetis "Correction Factor" approach
    /// (NetworkIO.VFOfreq, Setup → General → Calibration); mathematically
    /// identical to piHPSDR's `(10_000_000 + ppm_tenths) / 10_000_000`
    /// formula. ppm = (factor - 1.0) * 1e6.
    /// </summary>
    public double GetFrequencyCorrectionFactor()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            // LiteDB hydrates double-valued fields to 0.0 for older rows
            // that pre-date this field; treat that case as "unset" so
            // operators upgrading from a pre-#325 build don't see their
            // tuning silently jump to DC.
            return (e is null || e.FrequencyCorrectionFactor == 0.0)
                ? 1.0
                : e.FrequencyCorrectionFactor;
        }
    }

    /// <summary>
    /// Persists the operator's calibrated correction factor. Stored in the
    /// same single-row preferences entry as the other per-radio prefs.
    /// Setting to 1.0 is identical to "unset" (factory default).
    /// </summary>
    public void SetFrequencyCorrectionFactor(double factor)
    {
        lock (_sync)
        {
            var existing = _entries.FindAll().FirstOrDefault();
            if (existing is null)
            {
                _entries.Insert(new PreferredRadioEntry
                {
                    Board = HpsdrBoardKind.Unknown,
                    OverrideDetection = false,
                    FrequencyCorrectionFactor = factor,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.FrequencyCorrectionFactor = factor;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
        Changed?.Invoke();
    }

    public void Dispose() => _dbLease.Dispose();

}

public sealed class PreferredRadioEntry
{
    public int Id { get; set; }
    public HpsdrBoardKind Board { get; set; }
    public bool OverrideDetection { get; set; }
    /// <summary>Operator-selected variant for the 0x0A wire-byte alias
    /// family. LiteDB hydrates as <see cref="OrionMkIIVariant.G2"/>
    /// (the zero-default) for older rows that pre-date this field, which
    /// preserves Zeus' pre-#218 dispatch behaviour.</summary>
    public OrionMkIIVariant OrionMkIIVariant { get; set; }
    /// <summary>HL2 Band Volts PWM enable (issue #279). LiteDB hydrates as
    /// <c>false</c> for older rows that pre-date this field, which matches
    /// the shipping default.</summary>
    public bool EnableHl2BandVolts { get; set; }
    /// <summary>ANAN-G2/Saturn ADC dither enable. Nullable so upgraded rows
    /// can inherit the default-on value instead of silently disabling a radio
    /// option Thetis leaves enabled.</summary>
    public bool? G2AdcDitherEnabled { get; set; }
    /// <summary>ANAN-G2/Saturn ADC digital-output randomizer. Nullable for
    /// the same upgrade-default reason as <see cref="G2AdcDitherEnabled"/>.
    /// </summary>
    public bool? G2AdcRandomEnabled { get; set; }
    /// <summary>ANAN-G2/Saturn second-ADC RX step attenuator in dB. Nullable
    /// so upgraded rows inherit the hardware power-on default of 0 dB.</summary>
    public int? G2Rx1AttenuatorDb { get; set; }
    /// <summary>Per-radio frequency-correction factor (issue #325).
    /// Dimensionless multiplier near 1.0; 1.0 = uncalibrated. LiteDB
    /// hydrates as 0.0 for older rows that pre-date this field; the
    /// accessor treats 0.0 as "unset" and returns 1.0 to keep tuning
    /// behaviour bit-identical for upgrading operators.</summary>
    public double FrequencyCorrectionFactor { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

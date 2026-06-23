// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// FreeDV digital voice wire contracts. The FreeDV modem (Codec2 /
// freedv_api, LGPL-2.1) is loaded by Zeus via P/Invoke and inserted as a
// streaming filter in the RX/TX audio path. FreeDV is exposed to the
// operator as RxMode.FreeDv; these DTOs carry submode selection and live
// modem telemetry over the dedicated /api/freedv surface (kept off the
// positional StateDto record so the core wire format is untouched).

namespace Zeus.Contracts;

/// <summary>
/// FreeDV HF voice submodes carried over the air. Values are stable for prefs
/// persistence — append only. These map to upstream FREEDV_MODE_* constants
/// inside the DSP layer; the heavy LPCNet/2020 family is intentionally absent
/// (cross-platform/arm build stays dependency-free).
/// </summary>
public enum FreeDvSubmode : byte
{
    /// <summary>700D — OFDM/QPSK, LDPC, works to ~-2 dB SNR. Default.</summary>
    Mode700D = 0,
    /// <summary>700E — OFDM/QPSK, better fast-fading/multipath, low latency.</summary>
    Mode700E = 1,
    /// <summary>700C — coherent QPSK + diversity, no FEC (legacy interop).</summary>
    Mode700C = 2,
    /// <summary>1600 — DQPSK + pilot, Golay FEC (legacy interop).</summary>
    Mode1600 = 3,
    /// <summary>800XA — 4FSK 2 kHz, narrowband (legacy interop).</summary>
    Mode800XA = 4,
}

/// <summary>
/// Live FreeDV modem status, polled by the FreeDV panel. <see cref="NativeAvailable"/>
/// is false when libcodec2 could not be loaded (FreeDV mode then passes audio
/// through unchanged and the panel shows an install hint).
/// </summary>
public sealed record FreeDvStatusDto(
    bool NativeAvailable,
    bool Active,
    FreeDvSubmode Submode,
    bool Synced,
    double SnrDb,
    bool SquelchEnabled,
    double SnrSquelchThreshDb,
    int SpeechSampleRateHz,
    int ModemSampleRateHz,
    string? RxText,
    string? TxText,
    string? LibraryVersion);

/// <summary>Operator config for the FreeDV modem. Null fields leave the current value unchanged.</summary>
public sealed record FreeDvConfigRequest(
    FreeDvSubmode? Submode = null,
    bool? SquelchEnabled = null,
    double? SnrSquelchThreshDb = null,
    string? TxText = null);

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
    /// <summary>
    /// RADE V1 — neural "Radio Autoencoder" (FreeDV's flagship ML mode). NOT a
    /// codec2/freedv_open submode: it's a separate native modem (librade + FARGAN
    /// vocoder, complex IO, 16 kHz speech). Selectable but gated until the native
    /// RADE library is integrated — see the rade-v1-integration design.
    /// </summary>
    RadeV1 = 5,
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
    string? LibraryVersion,
    // True when auto submode detection is engaged: while unsynced the modem
    // cycles submodes until one locks. Submode reflects the live (possibly
    // scanner-chosen) mode.
    bool AutoDetect = false,
    // True when the native RADE (Radio Autoencoder) modem is available. False
    // until librade is integrated — selecting RadeV1 then runs no decoder
    // (passthrough), and the panel shows a "RADE not installed" state.
    bool RadeAvailable = false);

/// <summary>Operator config for the FreeDV modem. Null fields leave the current value unchanged.</summary>
public sealed record FreeDvConfigRequest(
    FreeDvSubmode? Submode = null,
    bool? SquelchEnabled = null,
    double? SnrSquelchThreshDb = null,
    string? TxText = null,
    bool? AutoDetect = null);

/// <summary>
/// One live station from the FreeDV Reporter network (qso.freedv.org). The
/// reporter aggregates stations running FreeDV-GUI / Zeus that opted into
/// spotting; Zeus mirrors the feed read-only ("view" role) for a click-to-tune
/// stations panel. <see cref="Sid"/> is the reporter's per-connection session id
/// (the dictionary key, stable for the life of a station's connection).
/// </summary>
public sealed record FreeDvStationDto(
    string Sid,
    string Callsign,
    string? GridSquare,
    long FreqHz,
    string Mode,
    bool Transmitting,
    bool RxOnly,
    string? Message,
    string? Version,
    double? LastRxSnr,
    string? LastRxCallsign,
    string? LastRxMode,
    string LastUpdate,     // ISO-8601 UTC, e.g. DateTime.UtcNow.ToString("o")
    string? ConnectTime);  // ISO-8601 UTC or null

/// <summary>
/// Snapshot of the FreeDV Reporter stations channel for the Stations panel.
/// <see cref="ConnectionState"/> mirrors the upstream Socket.IO link state
/// ("Disconnected" | "Connecting" | "Connected" | "Reconnecting") so the panel
/// can show a live/stale indicator; <see cref="Stations"/> is sorted by
/// frequency ascending. <see cref="Reporting"/> is true when the operator is
/// connected in the "report" role (on the public map); <see cref="MySid"/> is
/// the operator's own per-connection session id while reporting (null otherwise),
/// so the panel can highlight the operator's own row.
/// </summary>
public sealed record FreeDvStationsResponseDto(
    string ConnectionState,
    bool Enabled,
    IReadOnlyList<FreeDvStationDto> Stations,
    bool Reporting = false,
    string? MySid = null);

/// <summary>
/// Operator config for FreeDV Reporter "report" mode. Strictly opt-in: when
/// <see cref="ReportEnabled"/> is false (the default), or callsign / grid are
/// blank, Zeus stays a read-only "view" observer and broadcasts nothing. When
/// enabled with a callsign + Maidenhead grid, Zeus connects in the "report" role
/// and publishes the operator's callsign, grid, frequency and TX activity to the
/// public qso.freedv.org map. <see cref="Message"/> is an optional status text.
/// </summary>
public sealed record FreeDvReporterSettings(
    bool ReportEnabled = false,
    string Callsign = "",
    string GridSquare = "",
    string Message = "")
{
    public const int MaxGridLength = 6;
    public const int MaxMessageLength = 80;

    /// <summary>
    /// Trim/normalize so a hand-crafted POST or stale persisted row can't push
    /// junk to the public reporter: callsign upper-cased, grid trimmed/capped to
    /// a Maidenhead-shaped prefix, message trimmed/capped. Reporting still only
    /// engages when ReportEnabled is true AND both callsign and grid are present.
    /// </summary>
    public FreeDvReporterSettings Normalized() => this with
    {
        Callsign = (Callsign ?? "").Trim().ToUpperInvariant(),
        GridSquare = NormalizeGrid(GridSquare),
        Message = NormalizeMessage(Message),
    };

    /// <summary>True when the settings are sufficient to connect in report role.</summary>
    public bool CanReport =>
        ReportEnabled
        && !string.IsNullOrWhiteSpace(Callsign)
        && !string.IsNullOrWhiteSpace(GridSquare);

    private static string NormalizeGrid(string? grid)
    {
        var g = (grid ?? "").Trim();
        if (g.Length == 0) return "";
        // A Maidenhead locator is field/square[/subsquare]: two letters, two
        // digits, optionally two more letters. Keep only that leading run, cap
        // at six chars, and upper-case the field/subsquare so it round-trips
        // consistently. Anything that doesn't even start letter-letter is
        // dropped (so a typo can't broadcast a bogus location).
        if (g.Length > MaxGridLength) g = g[..MaxGridLength];
        if (g.Length < 2 || !char.IsLetter(g[0]) || !char.IsLetter(g[1])) return "";
        return g.ToUpperInvariant();
    }

    private static string NormalizeMessage(string? message)
    {
        var m = (message ?? "").Trim();
        return m.Length > MaxMessageLength ? m[..MaxMessageLength] : m;
    }
}

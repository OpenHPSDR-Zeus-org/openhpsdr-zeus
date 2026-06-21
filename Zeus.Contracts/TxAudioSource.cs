// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Contracts;

/// <summary>
/// The single mutually-exclusive TX-audio SOURCE for a connected radio
/// (external-ports plan, §2). Replaces the prior four-bool
/// <c>AudioFrontEndSelection</c> mic-PARAMETER model, which only set codec wire
/// bits and never switched the TX pipeline (host mic kept flowing regardless of
/// the operator's pick) and allowed illegal combinations (line-in AND balanced,
/// etc.). With this enum, illegal states are unrepresentable: exactly one source
/// is active at a time.
///
/// <see cref="Host"/> = 0 is the default and the universal fallback. A persisted
/// row that names a source the connected board can't honour (e.g. a balanced-XLR
/// selection after a G2→Hermes swap) is clamped back to <see cref="Host"/> at
/// connect, so the wire is never handed a source the hardware lacks.
///
/// MicBoost / MicBias / LineInGain are PARAMETERS OF a chosen source (carried
/// alongside in <c>AudioSettingsStore</c>), never peer toggles — they apply only
/// to whichever radio jack is selected and are suppressed entirely under
/// <see cref="Host"/>.
///
/// RED-LIGHT but ADDITIVE and back-compatible: the underlying byte values are
/// wire-stable and serialised into <c>zeus-prefs.db</c>; default 0 reproduces
/// today's behaviour bit-for-bit on every board.
/// </summary>
public enum TxAudioSource : byte
{
    /// <summary>Host-supplied TX audio (USB/network mic, TCI, WAV playback —
    /// whatever feeds the host TX pipeline today). The default and the only
    /// option on boards with no radio-audio jack (Hermes-Lite 2). The radio's
    /// own analog input is suppressed; the wire is byte-identical to today.</summary>
    Host = 0,

    /// <summary>The radio's analog 3.5 mm microphone jack. Honours the
    /// per-source <c>MicBoost</c> parameter on every codec/P2 board and
    /// <c>MicBias</c> on the bias-capable boards (200D / 7000DLE / 8000DLE /
    /// G2 / G2-1K / ANVELINA-PRO3).</summary>
    RadioMic = 1,

    /// <summary>The radio's analog 3.5 mm line-in jack. Honours the per-source
    /// 0..31 <c>LineInGain</c>. Offered on ANAN-200D and the 0x0A Saturn family
    /// only (Zeus has no P1 radio-mic receive path in v1, so pure-P1 codec
    /// boards do not expose it — §6 deviation note).</summary>
    RadioLineIn = 2,

    /// <summary>The radio's balanced XLR microphone input. Switchable on the
    /// Saturn-FPGA G2 / G2-1K only (<c>HasBalancedXlr</c>). Implies the XLR wire
    /// select; no separate "balanced" flag is persisted.</summary>
    RadioBalancedXlr = 3,
}

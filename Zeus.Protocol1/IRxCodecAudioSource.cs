// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Protocol1;

/// <summary>
/// Supplies one (L, R) s16 audio sample pair per call for the EP2
/// outbound 8-byte slot's L/R audio bytes (offsets 0..3). Used on boards
/// with an on-board audio codec (Hermes / Mercury / ANAN-class / OrionMkII
/// / G2E) so the operator can plug headphones into the radio's front-panel
/// jack and hear demodulated RX audio.
///
/// HermesLite2 has no on-board codec — these bytes are ignored by the radio.
/// On HL2 we simply don't register a source and the EP2 audio bytes stay
/// zero (the existing pre-#426 behaviour).
///
/// Implementations must be safe for the single reader (the Protocol1 TX
/// loop) — they're polled 63 times per USB frame, twice per Metis packet,
/// ~1500 calls/sec at the 750 packet/sec EP2 rate. Cross-thread writes
/// (from the DSP audio sink) need to be safe relative to that reader.
/// </summary>
public interface IRxCodecAudioSource
{
    /// <summary>
    /// Return the next (L, R) audio sample pair. Implementations that have
    /// no data return (0, 0); the wire bytes go out as silence, matching
    /// HL2 / pre-codec behaviour.
    /// </summary>
    (short L, short R) Next();
}

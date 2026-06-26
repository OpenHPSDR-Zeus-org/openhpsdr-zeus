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
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Protocol1;

/// <summary>
/// Supplies demodulated RX audio (mono, 48 kHz, s16) for the L/R slots of the
/// EP2 TX frame so a Protocol-1 radio's onboard codec drives its
/// speaker/headphone/line-out jacks. The same EP2 frame carries TX I/Q (via
/// <see cref="ITxIqSource"/>) in its other four bytes; this seam only owns the
/// two RX-audio channels.
///
/// The single concrete source is <see cref="RxAudioRing"/>, fed by the DSP RX
/// path host-side. Implementations must be safe for a single reader (the
/// Protocol-1 TX loop); the ring variant handles cross-thread writes from the
/// audio-publish side.
/// </summary>
public interface IRxAudioSource
{
    /// <summary>
    /// Fill <paramref name="dest"/> with up to <c>dest.Length</c> mono s16
    /// samples and return the count actually written. When the source is empty
    /// the return value is 0 and <paramref name="dest"/> is left untouched — the
    /// caller then leaves the L/R slots zero, byte-identical to a radio that
    /// carries no RX audio at all.
    /// </summary>
    int Read(Span<short> dest);
}

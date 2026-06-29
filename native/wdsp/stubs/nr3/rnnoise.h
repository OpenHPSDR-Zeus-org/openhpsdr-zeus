/*
 * SPDX-License-Identifier: GPL-2.0-or-later
 *
 * Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
 * Copyright (C) 2025-2026 Brian Keating (EI6LF),
 *                         Douglas J. Cerrato (KB2UKA),
 *                         Christian Suarez (N9WAR), and contributors.
 *
 * This program is free software: you can redistribute it and/or modify it
 * under the terms of the GNU General Public License as published by the
 * Free Software Foundation, either version 2 of the License, or (at your
 * option) any later version. See the LICENSE file at the root of this
 * repository for the full text, or https://www.gnu.org/licenses/.
 *
 * Zeus is an independent reimplementation in .NET — not a fork. Its
 * Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
 * TX behaviour were informed by studying the Thetis project
 * (https://github.com/ramdor/Thetis), the authoritative reference
 * implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
 * the Thetis contributors whose work made this possible:
 *
 *   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
 *   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
 *   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
 *   Doug Wigley (W5WC),        FlexRadio Systems,
 *   Richard Allen (W5SD),      Joe Torrey (WD5Y),
 *   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT).
 *
 * Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
 * and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
 * here. See ATTRIBUTIONS.md at the repository root for the full provenance
 * statement and per-component attribution.
 *
 * WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
 * (NR0V), distributed under GPL v2 or later.
 *
 * Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
 * License for details.
 */

/* rnnoise.h — Zeus stub
 *
 * Minimal stand-in for <rnnoise.h> used when building WDSP without the
 * RNNoise (NR3) backend. Only the opaque DenoiseState type is needed by
 * rnnr.h's struct definition; all runtime calls live in rnnr_stub.c.
 */

#ifndef ZEUS_RNNOISE_STUB_H
#define ZEUS_RNNOISE_STUB_H

typedef struct DenoiseState DenoiseState;

#endif

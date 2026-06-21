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

/* rnnr_stub.c — Zeus NR3 stub
 *
 * No-op replacement for rnnr.c used by the MVP build. Upstream rnnr.c
 * links against RNNoise; we defer that dependency and compile this file
 * instead (see native/wdsp/CMakeLists.txt, option WDSP_WITH_NR3_NR4).
 * Struct layout matches rnnr.h so that RXA.c's accesses — notably
 * `rxa[ch].rnnr.p->run` — remain well-defined. The `run` field stays
 * zero, so the NR3 path in RXA.c's ResCheck is never taken.
 */

#include "comm.h"

RNNR create_rnnr(int run, int position, int size, double *in, double *out, int rate) {
    (void)run; (void)position; (void)size; (void)in; (void)out; (void)rate;
    rnnr *a = (rnnr *)calloc(1, sizeof(rnnr));
    return a;
}

void destroy_rnnr(RNNR a) {
    if (a) free(a);
}

void setSize_rnnr(RNNR a, int size)            { (void)a; (void)size; }
void setBuffers_rnnr(RNNR a, double *in, double *out) { (void)a; (void)in; (void)out; }
void xrnnr(RNNR a, int pos)                    { (void)a; (void)pos; }
void setSamplerate_rnnr(RNNR a, int rate)      { (void)a; (void)rate; }

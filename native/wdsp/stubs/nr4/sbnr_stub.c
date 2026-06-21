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

/* sbnr_stub.c — Zeus NR4 stub
 *
 * No-op replacement for sbnr.c used by the MVP build. Upstream sbnr.c
 * links against libspecbleach; we defer that dependency and compile this
 * file instead (see native/wdsp/CMakeLists.txt, option WDSP_WITH_NR3_NR4).
 * Struct layout matches sbnr.h so that RXA.c's accesses — notably
 * `rxa[ch].sbnr.p->run` — remain well-defined. `run` stays zero, so the
 * NR4 path is never taken.
 */

#include "comm.h"

SBNR create_sbnr(int run, int position, int size, double *in, double *out, int rate) {
    (void)run; (void)position; (void)size; (void)in; (void)out; (void)rate;
    sbnr *a = (sbnr *)calloc(1, sizeof(sbnr));
    return a;
}

void destroy_sbnr(SBNR a) {
    if (a) free(a);
}

void setSize_sbnr(SBNR a, int size)            { (void)a; (void)size; }
void setBuffers_sbnr(SBNR a, double *in, double *out) { (void)a; (void)in; (void)out; }
void xsbnr(SBNR a, int pos)                    { (void)a; (void)pos; }
void setSamplerate_sbnr(SBNR a, int rate)      { (void)a; (void)rate; }

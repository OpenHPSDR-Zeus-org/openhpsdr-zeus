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

/* wdsp_export.h
 *
 * Cross-platform symbol-visibility macro for the WDSP shared library.
 *
 * On Windows we emit __declspec(dllexport) when building the library;
 * on POSIX (Linux, macOS) we combine -fvisibility=hidden at the compiler
 * level with __attribute__((visibility("default"))) on exported entry points
 * so the exported ABI is a deliberate subset of the source's static surface.
 *
 * Replaces the Windows-only `PORT` macro in upstream WDSP (`comm.h`).
 */

#ifndef WDSP_EXPORT_H
#define WDSP_EXPORT_H

#if defined(_WIN32)
#  define WDSP_EXPORT __declspec(dllexport)
#else
#  define WDSP_EXPORT __attribute__((visibility("default")))
#endif

#endif /* WDSP_EXPORT_H */

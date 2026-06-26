// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// No-op stub for osdwspr_ — the WSPR Ordered-Statistics Decoder. Upstream wsprd
// implements OSD in Fortran (osdwspr.f90); it is an OPTIONAL deep-decode pass
// (the `-o` flag, disabled by default) that recovers a few extra very weak
// signals. Zeus does not pull in a Fortran toolchain across all platforms, so
// we stub the symbol: the decoder links and runs the normal Fano/Jelinek path,
// and as long as OSD depth is left at its default-off the stub is never called.
//
// Signature matches the call in wsprd.c:
//   osdwspr_(fsymbs, apmask, &ndepth, cw, &nhardmin, &dmin)
// Returning nhardmin < 0 marks "no OSD decode" so any result is discarded.

void osdwspr_(float* fsymbs, char* apmask, int* ndepth,
              char* cw, int* nhardmin, float* dmin)
{
    (void)fsymbs;
    (void)apmask;
    (void)ndepth;
    (void)cw;
    if (nhardmin) *nhardmin = -1;
    if (dmin) *dmin = 0.0f;
}

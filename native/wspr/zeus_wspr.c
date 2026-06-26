// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// zeus_wspr — shim over the vendored wsprd (GPL-3). See zeus_wspr.h.

#include "zeus_wspr.h"

#include <stdlib.h>
#include <string.h>
#ifndef _USE_MATH_DEFINES
#define _USE_MATH_DEFINES
#endif
#include <math.h>
#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

// Vendored encoder (wsprsim_utils.c): packs the 50-bit message, applies the
// K=32 r=1/2 convolutional FEC, interleaves, and merges the sync vector into
// 162 4-FSK symbols. Returns 1 on success, 0 on failure.
extern int get_wspr_channel_symbols(const char* message, char* hashtab,
                                    char* loctab, unsigned char* symbols);

// The vendored encoder references a `printdata` global normally defined in
// wsprd.c. In the encode-only lib, wsprd.c isn't linked, so provide it here.
// When the decoder (wsprd.c) IS linked, it owns the definition — guard against
// a duplicate via ZEUS_WSPR_LINK_WSPRD.
#ifndef ZEUS_WSPR_LINK_WSPRD
int printdata = 0;
#endif

// Callsign-hash table sizes the vendored code expects (32768 entries).
#define ZW_HASHTAB_BYTES (32768 * 13)
#define ZW_LOCTAB_BYTES  (32768 * 5)

int32_t zeus_wspr_encode(const char* message, uint8_t* symbols, int32_t max_symbols)
{
    if (message == NULL || symbols == NULL || max_symbols < ZEUS_WSPR_NSYM)
        return -1;

    // Hashed-callsign tables are only meaningful for type-2/3 messages; a zeroed
    // pair is correct for standard messages and harmless otherwise.
    char* hashtab = (char*)calloc(ZW_HASHTAB_BYTES, 1);
    char* loctab = (char*)calloc(ZW_LOCTAB_BYTES, 1);
    if (hashtab == NULL || loctab == NULL)
    {
        free(hashtab);
        free(loctab);
        return -2;
    }

    unsigned char sym[ZEUS_WSPR_NSYM];
    int rc = get_wspr_channel_symbols(message, hashtab, loctab, sym);

    free(hashtab);
    free(loctab);

    if (rc != 1)
        return -3;

    for (int i = 0; i < ZEUS_WSPR_NSYM; ++i)
        symbols[i] = sym[i];
    return ZEUS_WSPR_NSYM;
}

int32_t zeus_wspr_synth(const uint8_t* symbols, int32_t n_sym,
                        float f0_hz, int32_t sample_rate,
                        float* audio, int32_t max_samples)
{
    if (symbols == NULL || audio == NULL || n_sym <= 0 || sample_rate <= 0)
        return -1;

    // Samples per symbol at this rate (canonical 8192 @ 12 kHz).
    int n_spsym = (int)(sample_rate * ZEUS_WSPR_SYMBOL_PERIOD_S + 0.5);
    long total = (long)n_spsym * n_sym;
    if (total > max_samples)
        return -2;

    double two_pi = 2.0 * M_PI;
    double phase = 0.0;
    long k = 0;
    for (int s = 0; s < n_sym; ++s)
    {
        uint8_t tone = symbols[s] & 0x3;
        double freq = (double)f0_hz + tone * ZEUS_WSPR_TONE_SPACING_HZ;
        double dphi = two_pi * freq / sample_rate;
        for (int i = 0; i < n_spsym; ++i, ++k)
        {
            audio[k] = (float)sin(phase);
            phase += dphi;
            if (phase > two_pi) phase -= two_pi;
        }
    }

    // Short cosine ramps at the very start/end to limit key clicks.
    int ramp = n_spsym / 8;
    for (int i = 0; i < ramp && i < total; ++i)
    {
        float env = (float)((1.0 - cos(M_PI * i / ramp)) / 2.0);
        audio[i] *= env;
        audio[total - 1 - i] *= env;
    }
    return (int32_t)total;
}

const char* zeus_wspr_version(void)
{
    return "zeus_wspr 0.2 (K1JT/K9AN wsprd GPL-3, encode + synth)";
}

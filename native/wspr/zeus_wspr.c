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

// Vendored encoder (wsprsim_utils.c): packs the 50-bit message, applies the
// K=32 r=1/2 convolutional FEC, interleaves, and merges the sync vector into
// 162 4-FSK symbols. Returns 1 on success, 0 on failure.
extern int get_wspr_channel_symbols(const char* message, char* hashtab,
                                    char* loctab, unsigned char* symbols);

// The vendored encoder/decoder reference a `printdata` global normally defined
// in wsprd.c (the CLI main, not compiled into this lib). Provide it here.
int printdata = 0;

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

const char* zeus_wspr_version(void)
{
    return "zeus_wspr 0.1 (K1JT/K9AN wsprd GPL-3, encode)";
}

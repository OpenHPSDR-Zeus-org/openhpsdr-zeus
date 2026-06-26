// SPDX-License-Identifier: GPL-2.0-or-later
//
// zeus_wspr self-test: encode a known WSPR message and verify the output is a
// standards-compliant symbol sequence. The strongest check is that the per-
// symbol sync bits (symbol & 1) match the fixed, published WSPR sync vector —
// an EXTERNAL reference, so this validates the encoder rather than itself.

#include <stdio.h>
#include "../zeus_wspr.h"

// The canonical WSPR sync vector (first 32 of 162). Every WSPR transmission
// carries this exact pattern in the low bit of each 4-FSK symbol.
static const unsigned char WSPR_SYNC_HEAD[32] = {
    1, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0,
    0, 0, 1, 0, 0, 1, 0, 1, 1, 1, 1, 0, 0, 0, 0, 0,
};

int main(void)
{
    unsigned char sym[ZEUS_WSPR_NSYM];
    int n = zeus_wspr_encode("KB2UKA FN12 30", sym, ZEUS_WSPR_NSYM);
    if (n != ZEUS_WSPR_NSYM)
    {
        fprintf(stderr, "FAIL: encode returned %d, expected %d\n", n, ZEUS_WSPR_NSYM);
        return 1;
    }

    // Every symbol is a valid 4-FSK tone index.
    for (int i = 0; i < ZEUS_WSPR_NSYM; ++i)
    {
        if (sym[i] > 3)
        {
            fprintf(stderr, "FAIL: symbol %d out of range: %d\n", i, sym[i]);
            return 1;
        }
    }

    // Sync bits must match the published WSPR sync vector.
    for (int i = 0; i < 32; ++i)
    {
        if ((sym[i] & 1) != WSPR_SYNC_HEAD[i])
        {
            fprintf(stderr, "FAIL: sync bit %d = %d, expected %d\n",
                    i, sym[i] & 1, WSPR_SYNC_HEAD[i]);
            return 1;
        }
    }

    // Determinism: a second encode of the same message yields identical symbols.
    unsigned char sym2[ZEUS_WSPR_NSYM];
    zeus_wspr_encode("KB2UKA FN12 30", sym2, ZEUS_WSPR_NSYM);
    for (int i = 0; i < ZEUS_WSPR_NSYM; ++i)
    {
        if (sym[i] != sym2[i])
        {
            fprintf(stderr, "FAIL: non-deterministic at %d\n", i);
            return 1;
        }
    }

    printf("OK: WSPR encode 162 symbols, sync vector matches, deterministic\n");
    return 0;
}

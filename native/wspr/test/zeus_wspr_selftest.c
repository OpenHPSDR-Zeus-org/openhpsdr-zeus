// SPDX-License-Identifier: GPL-2.0-or-later
//
// zeus_wspr self-test: encode a known WSPR message and verify the output is a
// standards-compliant symbol sequence. The strongest check is that the per-
// symbol sync bits (symbol & 1) match the fixed, published WSPR sync vector —
// an EXTERNAL reference, so this validates the encoder rather than itself.

#include <stdio.h>
#include <stdlib.h>
#include <math.h>
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

    // Synthesize the 4-FSK audio for the encoded symbols and sanity-check it.
    const int sr = 12000;
    const int nspsym = (int)(sr * ZEUS_WSPR_SYMBOL_PERIOD_S + 0.5);
    const long total = (long)nspsym * ZEUS_WSPR_NSYM;
    float* audio = (float*)malloc(sizeof(float) * total);
    if (audio == NULL) { fprintf(stderr, "FAIL: alloc\n"); return 1; }
    int32_t ns = zeus_wspr_synth(sym, ZEUS_WSPR_NSYM, 1500.0f, sr, audio, (int32_t)total);
    if (ns != (int32_t)total)
    {
        fprintf(stderr, "FAIL: synth returned %d, expected %ld\n", ns, total);
        free(audio);
        return 1;
    }
    double sumsq = 0.0;
    for (long i = 0; i < total; ++i)
    {
        if (audio[i] < -1.0001f || audio[i] > 1.0001f)
        {
            fprintf(stderr, "FAIL: synth sample %ld out of range: %f\n", i, audio[i]);
            free(audio);
            return 1;
        }
        sumsq += (double)audio[i] * audio[i];
    }
    free(audio);
    double rms = sqrt(sumsq / total);
    if (rms < 0.5)  // a full-amplitude sine has rms ~0.707
    {
        fprintf(stderr, "FAIL: synth rms too low: %f\n", rms);
        return 1;
    }

    printf("OK: WSPR encode (sync vector matches, deterministic) + synth %ld samples rms=%.3f\n",
           total, rms);
    return 0;
}

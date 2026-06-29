// SPDX-License-Identifier: GPL-2.0-or-later
//
// WSPR decode round-trip gate, through the production zeus_wspr_decode ABI:
// encode a known message -> synthesize its 4-FSK audio -> place it in a 114 s
// buffer -> zeus_wspr_decode() -> assert the message comes back in a spot.
// Self-contained (no external WSPR sample). This is the WSPR decode-correctness
// CI gate. POSIX-only for now (the decode ABI uses mkdtemp/pthreads).

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "../zeus_wspr.h"

int main(void)
{
    const char* msg = "KB2UKA FN12 30";
    const int sr = 12000;

    unsigned char sym[ZEUS_WSPR_NSYM];
    if (zeus_wspr_encode(msg, sym, ZEUS_WSPR_NSYM) != ZEUS_WSPR_NSYM)
    {
        fprintf(stderr, "FAIL: encode\n");
        return 1;
    }

    static float synth[ZEUS_WSPR_NSYM * 8192];
    int ns = zeus_wspr_synth(sym, ZEUS_WSPR_NSYM, 1500.0f, sr, synth, ZEUS_WSPR_NSYM * 8192);
    if (ns <= 0) { fprintf(stderr, "FAIL: synth\n"); return 1; }

    long total = 114L * 12000;            // one WSPR slot at 12 kHz
    float* buf = (float*)calloc(total, sizeof(float));
    if (!buf) { fprintf(stderr, "FAIL: alloc\n"); return 1; }
    long off = 12000;                     // signal starts ~1 s in
    for (long i = 0; i < ns && off + i < total; ++i)
        buf[off + i] = synth[i] * 0.5f;

    zeus_wspr_spot_t spots[16];
    int32_t nspots = zeus_wspr_decode(buf, (int32_t)total, sr, 14.0956, spots, 16);
    free(buf);

    if (nspots < 0) { fprintf(stderr, "FAIL: decode returned %d\n", nspots); return 1; }

    int found = 0;
    for (int i = 0; i < nspots; ++i)
    {
        fprintf(stderr, "spot: %5.1f dB  %+4.1f s  %.6f MHz  drift %d  %s\n",
                spots[i].snr_db, spots[i].dt_sec, spots[i].freq_mhz,
                spots[i].drift_hz, spots[i].message);
        if (strstr(spots[i].message, "KB2UKA")) found = 1;
    }

    fprintf(stderr, "\nWSPR decode (via zeus_wspr_decode): %s\n",
            found ? "DECODED KB2UKA — PASS" : "no decode — FAIL");
    return found ? 0 : 1;
}

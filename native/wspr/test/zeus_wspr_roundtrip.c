// SPDX-License-Identifier: GPL-2.0-or-later
//
// WSPR decode round-trip gate: encode a known message -> synthesize its 4-FSK
// audio -> write a 114 s / 12 kHz WAV -> run the vendored wsprd decoder ->
// assert the message decodes back. This is the WSPR decode-correctness CI gate
// (self-contained — no external WSPR sample needed). POSIX-only for now (uses
// dup2 to capture the decoder's stdout); the cross-platform production path
// returns decodes via a struct instead of stdout.

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <unistd.h>
#include "../zeus_wspr.h"

// wsprd.c's main(), renamed at compile time (-Dmain=wsprd_cli_main).
extern int wsprd_cli_main(int argc, char* argv[]);

static void wr16(FILE* f, uint16_t v) { fputc(v & 255, f); fputc(v >> 8, f); }
static void wr32(FILE* f, uint32_t v) { for (int i = 0; i < 4; i++) fputc((v >> (8 * i)) & 255, f); }

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

    static float audio[ZEUS_WSPR_NSYM * 8192];
    int ns = zeus_wspr_synth(sym, ZEUS_WSPR_NSYM, 1500.0f, sr, audio, ZEUS_WSPR_NSYM * 8192);
    if (ns <= 0) { fprintf(stderr, "FAIL: synth\n"); return 1; }

    long total = 114L * 12000;                 // wsprd expects 114 s
    int16_t* pcm = (int16_t*)calloc(total, 2);
    if (!pcm) { fprintf(stderr, "FAIL: alloc\n"); return 1; }
    long off = 12000;                          // signal starts ~1 s in
    for (long i = 0; i < ns && off + i < total; ++i)
        pcm[off + i] = (int16_t)(audio[i] * 0.5f * 32767);

    FILE* f = fopen("wspr_rt.wav", "wb");
    if (!f) { fprintf(stderr, "FAIL: wav open\n"); free(pcm); return 1; }
    uint32_t dbytes = (uint32_t)(total * 2);
    fwrite("RIFF", 1, 4, f); wr32(f, 36 + dbytes); fwrite("WAVE", 1, 4, f);
    fwrite("fmt ", 1, 4, f); wr32(f, 16); wr16(f, 1); wr16(f, 1);
    wr32(f, 12000); wr32(f, 12000 * 2); wr16(f, 2); wr16(f, 16);
    fwrite("data", 1, 4, f); wr32(f, dbytes);
    fwrite(pcm, 2, total, f); fclose(f); free(pcm);

    // Capture the decoder's stdout.
    fflush(stdout);
    int saved = dup(1);
    if (!freopen("wspr_rt_out.txt", "w", stdout)) { fprintf(stderr, "FAIL: redirect\n"); return 1; }
    char* argv[] = { "wsprd", "-f", "14.0956", "wspr_rt.wav", NULL };
    wsprd_cli_main(4, argv);
    fflush(stdout);
    dup2(saved, 1);
    close(saved);

    FILE* o = fopen("wspr_rt_out.txt", "r");
    char line[512];
    int found = 0;
    while (o && fgets(line, sizeof line, o))
    {
        fputs(line, stderr);
        if (strstr(line, "KB2UKA")) found = 1;
    }
    if (o) fclose(o);

    fprintf(stderr, "\nWSPR round-trip: %s\n", found ? "DECODED KB2UKA — PASS" : "no decode — FAIL");
    return found ? 0 : 1;
}

// SPDX-License-Identifier: GPL-2.0-or-later
//
// zeus_ft8 self-test: decode the bundled reference WAV corpus through the
// zeus_ft8 ABI and report decoded-vs-expected per slot plus a corpus total.
// The expected counts come from the matching .txt answer keys (WSJT-X output).
//
// This is the objective decode-correctness gate: it runs in CI on every
// platform and proves the shim decodes real FT8 audio, not just that it links.

#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <dirent.h>

#include "../zeus_ft8.h"
#include "common/wave.h"

#define MAX_SAMPLES (15 * 12000)
#define MAX_DECODES 64

static int count_lines(const char* path)
{
    FILE* f = fopen(path, "r");
    if (!f) return -1;
    int lines = 0, c, last = '\n';
    while ((c = fgetc(f)) != EOF) { if (c == '\n') ++lines; last = c; }
    if (last != '\n') ++lines; // last line without trailing newline
    fclose(f);
    return lines;
}

int main(int argc, char** argv)
{
    if (argc < 2)
    {
        fprintf(stderr, "usage: %s <wav-dir>\n", argv[0]);
        return 2;
    }
    const char* dir = argv[1];

    DIR* d = opendir(dir);
    if (!d) { fprintf(stderr, "cannot open %s\n", dir); return 2; }

    char names[256][256];
    int n_files = 0;
    struct dirent* ent;
    while ((ent = readdir(d)) && n_files < 256)
    {
        size_t len = strlen(ent->d_name);
        if (len > 4 && 0 == strcmp(ent->d_name + len - 4, ".wav"))
            snprintf(names[n_files++], 256, "%s", ent->d_name);
    }
    closedir(d);

    // Stable order so output diffs are readable.
    for (int i = 0; i < n_files; ++i)
        for (int j = i + 1; j < n_files; ++j)
            if (strcmp(names[i], names[j]) > 0)
            { char t[256]; strcpy(t, names[i]); strcpy(names[i], names[j]); strcpy(names[j], t); }

    static float signal[MAX_SAMPLES];
    zeus_ft8_decode_t out[MAX_DECODES];

    int tot1 = 0, totM = 0, total_exp = 0, slots_with_key = 0;
    printf("%-28s %7s %7s %8s\n", "slot", "single", "multi", "expected");
    printf("-------------------------------------------------------\n");

    for (int i = 0; i < n_files; ++i)
    {
        char wav[512], txt[512], expbuf[16];
        snprintf(wav, sizeof(wav), "%s/%s", dir, names[i]);
        snprintf(txt, sizeof(txt), "%s/%.*s.txt", dir, (int)(strlen(names[i]) - 4), names[i]);

        int num_samples = MAX_SAMPLES, sample_rate = 12000;
        if (load_wav(signal, &num_samples, &sample_rate, wav) < 0)
        {
            printf("%-28s   LOAD-FAIL\n", names[i]);
            continue;
        }

        // Single-pass (NORMAL) and multi-pass (MULTI, 3 passes) on the same slot.
        zeus_ft8_ctx* c1 = zeus_ft8_ctx_create();
        int got1 = zeus_ft8_decode(c1, signal, num_samples, sample_rate,
                                   ZEUS_FT8_PROTO_FT8, 1, out, MAX_DECODES);
        zeus_ft8_ctx_destroy(c1);

        zeus_ft8_ctx* cM = zeus_ft8_ctx_create();
        int gotM = zeus_ft8_decode(cM, signal, num_samples, sample_rate,
                                   ZEUS_FT8_PROTO_FT8, 3, out, MAX_DECODES);
        zeus_ft8_ctx_destroy(cM);

        int exp = count_lines(txt);
        if (exp >= 0) { tot1 += got1; totM += gotM; total_exp += exp; ++slots_with_key; }
        snprintf(expbuf, sizeof(expbuf), "%d", exp);
        printf("%-28s %7d %7d %8s\n", names[i], got1, gotM, exp >= 0 ? expbuf : "-");
    }

    printf("-------------------------------------------------------\n");
    printf("CORPUS single-pass: %d / %d (%.0f%%)\n", tot1, total_exp,
           total_exp > 0 ? 100.0 * tot1 / total_exp : 0.0);
    printf("CORPUS multi-pass : %d / %d (%.0f%%)   [+%d decodes over single]\n",
           totM, total_exp, total_exp > 0 ? 100.0 * totM / total_exp : 0.0, totM - tot1);

    // Gates:
    //  - must decode something;
    //  - multi-pass must never regress below single-pass (subtraction only ever
    //    reveals more, never fewer — guards against a broken subtract step);
    //  - corpus decode rate must clear a floor (current ~76%; 65% leaves margin
    //    for minor per-platform FFT/rounding differences without redding CI).
    double rate = total_exp > 0 ? (double)totM / total_exp : 0.0;
    if (totM == 0) { fprintf(stderr, "FAIL: zero decodes across corpus\n"); return 1; }
    if (totM < tot1) { fprintf(stderr, "FAIL: multi-pass regressed vs single-pass\n"); return 1; }
    if (rate < 0.65) { fprintf(stderr, "FAIL: corpus decode rate %.0f%% below 65%% floor\n", rate * 100); return 1; }
    return 0;
}

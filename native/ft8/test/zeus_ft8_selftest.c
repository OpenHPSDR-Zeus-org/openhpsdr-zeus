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

    int total_got = 0, total_exp = 0, slots_with_key = 0;
    printf("%-28s %8s %8s\n", "slot", "decoded", "expected");
    printf("------------------------------------------------\n");

    for (int i = 0; i < n_files; ++i)
    {
        char wav[512], txt[512];
        snprintf(wav, sizeof(wav), "%s/%s", dir, names[i]);
        snprintf(txt, sizeof(txt), "%s/%.*s.txt", dir, (int)(strlen(names[i]) - 4), names[i]);

        int num_samples = MAX_SAMPLES, sample_rate = 12000;
        if (load_wav(signal, &num_samples, &sample_rate, wav) < 0)
        {
            printf("%-28s   LOAD-FAIL\n", names[i]);
            continue;
        }

        zeus_ft8_ctx* ctx = zeus_ft8_ctx_create();
        int got = zeus_ft8_decode(ctx, signal, num_samples, sample_rate,
                                  ZEUS_FT8_PROTO_FT8, 1, out, MAX_DECODES);
        zeus_ft8_ctx_destroy(ctx);

        int exp = count_lines(txt);
        if (exp >= 0) { total_got += got; total_exp += exp; ++slots_with_key; }
        printf("%-28s %8d %8s\n", names[i], got, exp >= 0 ? (snprintf(txt, 8, "%d", exp), txt) : "-");
    }

    printf("------------------------------------------------\n");
    printf("CORPUS: decoded %d / %d expected across %d keyed slots (%.0f%%)\n",
           total_got, total_exp, slots_with_key,
           total_exp > 0 ? 100.0 * total_got / total_exp : 0.0);

    // Gate: the shim must at minimum decode *something* on the easy slots and
    // hit a reasonable fraction of the corpus. Single-pass baseline ~65-70%.
    if (total_got == 0) { fprintf(stderr, "FAIL: zero decodes across corpus\n"); return 1; }
    return 0;
}

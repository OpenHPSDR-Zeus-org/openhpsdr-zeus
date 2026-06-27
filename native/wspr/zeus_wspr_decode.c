// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// zeus_wspr_decode — production decode ABI over the vendored wsprd (GPL-3),
// keeping the vendored source PRISTINE. wsprd's decode lives in its CLI main()
// (renamed to wsprd_cli_main at build time) which reads a WAV and writes spots
// to `<data_dir>/wspr_spots.txt`. We drive it via a per-call temp directory
// (no global stdout manipulation — wsprd's stdout prints are harmless log
// noise) and parse the result file. Serialised with a mutex because the
// vendored decoder has process-global state; WSPR decodes once per 120 s so
// serialising across RX slices is fine.
//
// POSIX implementation (mkdtemp / dirent / pthreads) — compiled only in the
// non-Windows decode target. A Windows port (GetTempPath + the MSVC shims the
// FT8 lib uses) lands with the cross-platform WSPR build.

#include "zeus_wspr.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <dirent.h>
#include <unistd.h>
#include <pthread.h>

extern int wsprd_cli_main(int argc, char* argv[]);

static pthread_mutex_t g_wspr_lock = PTHREAD_MUTEX_INITIALIZER;

static void wr16(FILE* f, uint16_t v) { fputc(v & 255, f); fputc(v >> 8, f); }
static void wr32(FILE* f, uint32_t v) { for (int i = 0; i < 4; i++) fputc((v >> (8 * i)) & 255, f); }

// Remove every file in `dir`, then the dir itself.
static void rmdir_recursive(const char* dir)
{
    DIR* d = opendir(dir);
    if (d)
    {
        struct dirent* e;
        char path[1024];
        while ((e = readdir(d)) != NULL)
        {
            if (strcmp(e->d_name, ".") == 0 || strcmp(e->d_name, "..") == 0) continue;
            snprintf(path, sizeof path, "%s/%s", dir, e->d_name);
            unlink(path);
        }
        closedir(d);
    }
    rmdir(dir);
}

int32_t zeus_wspr_decode(const float* samples, int32_t n, int32_t sample_rate,
                         double dial_freq_mhz, zeus_wspr_spot_t* out, int32_t max_results)
{
    if (samples == NULL || out == NULL || n <= 0 || sample_rate <= 0 || max_results <= 0)
        return -1;
    // The vendored readwavfile reads a fixed 114 s of 12 kHz int16 after a
    // 44-byte header; it ignores the WAV's declared rate. So require 12 kHz.
    if (sample_rate != 12000)
        return -3;

    pthread_mutex_lock(&g_wspr_lock);

    char dir[] = "/tmp/zeus_wspr_XXXXXX";
    if (mkdtemp(dir) == NULL)
    {
        pthread_mutex_unlock(&g_wspr_lock);
        return -2;
    }

    char wav[512], spots[512], freqstr[32];
    snprintf(wav, sizeof wav, "%s/in.wav", dir);
    snprintf(spots, sizeof spots, "%s/wspr_spots.txt", dir);
    snprintf(freqstr, sizeof freqstr, "%.6f", dial_freq_mhz);

    // Write a 12 kHz mono 16-bit WAV (114 s). Extra samples are ignored by the
    // decoder; a short buffer is zero-padded inside readwavfile.
    long want = 114L * 12000;
    long ncopy = (n < want) ? n : want;
    FILE* f = fopen(wav, "wb");
    if (f == NULL) { rmdir_recursive(dir); pthread_mutex_unlock(&g_wspr_lock); return -2; }
    uint32_t dbytes = (uint32_t)(want * 2);
    fwrite("RIFF", 1, 4, f); wr32(f, 36 + dbytes); fwrite("WAVE", 1, 4, f);
    fwrite("fmt ", 1, 4, f); wr32(f, 16); wr16(f, 1); wr16(f, 1);
    wr32(f, 12000); wr32(f, 12000 * 2); wr16(f, 2); wr16(f, 16);
    fwrite("data", 1, 4, f); wr32(f, dbytes);
    for (long i = 0; i < want; ++i)
    {
        float v = (i < ncopy) ? samples[i] : 0.0f;
        if (v > 1.0f) v = 1.0f; else if (v < -1.0f) v = -1.0f;
        wr16(f, (uint16_t)(int16_t)(v * 32767.0f));
    }
    fclose(f);

    char* argv[] = { "wsprd", "-f", freqstr, "-a", dir, wav, NULL };
    wsprd_cli_main(6, argv);

    // Parse <dir>/wspr_spots.txt:
    //   date time 10*sync snr dt freq  <message:22>  drift cycles ...
    int32_t count = 0;
    FILE* sp = fopen(spots, "r");
    if (sp != NULL)
    {
        char line[512];
        while (count < max_results && fgets(line, sizeof line, sp))
        {
            char date[24], tm[24], sync[24];
            float snr = 0, dt = 0;
            double freq = 0;
            int pos = 0;
            if (sscanf(line, "%23s %23s %23s %f %f %lf %n",
                       date, tm, sync, &snr, &dt, &freq, &pos) == 6 && pos > 0)
            {
                const char* m = line + pos;       // start of the %-22s message field
                char msg[28];
                int j = 0;
                for (int k = 0; k < 22 && m[k] && m[k] != '\n'; ++k) msg[j++] = m[k];
                msg[j] = '\0';
                while (j > 0 && msg[j - 1] == ' ') msg[--j] = '\0';

                // The message is a fixed-width %-22s field, so drift follows at
                // offset 22 (only parse it if the line is actually that long).
                int drift = 0;
                if (strlen(m) >= 22) sscanf(m + 22, " %d", &drift);

                out[count].snr_db = snr;
                out[count].dt_sec = dt;
                out[count].freq_mhz = (float)freq;
                out[count].drift_hz = drift;
                strncpy(out[count].message, msg, sizeof out[count].message - 1);
                out[count].message[sizeof out[count].message - 1] = '\0';
                ++count;
            }
        }
        fclose(sp);
    }

    rmdir_recursive(dir);
    pthread_mutex_unlock(&g_wspr_lock);
    return count;
}

/* SPDX-License-Identifier: BSD-2-Clause
 *
 * zeus_rade_test — validates the zeus_rade shim end-to-end.
 *
 * Default (decode):   reads interleaved complex IQ (RADE_COMP) @ 8 kHz from
 *   stdin, decodes via the shim, writes int16 PCM @ 16 kHz to stdout.
 *
 *     sox FDV_offair.wav -r 8000 -e float -b 32 -c 1 -t raw - \
 *       | real2iq | zeus_rade_test > out.pcm
 *     sox -t .s16 -r 16000 -c 1 out.pcm decoded.wav
 *
 * Loopback (encode->decode): `zeus_rade_test loopback [CALLSIGN]` synthesizes a
 *   few seconds of 16 kHz speech, ENCODES it to the modem waveform (real part of
 *   the TX IQ), appends an End-of-Over frame carrying CALLSIGN, then DECODES the
 *   whole stream back through the same modem. It reports RX sync ticks, decoded
 *   PCM, and the recovered callsign — a self-contained proof that the TX path
 *   produces a valid, FreeDV-GUI-compatible RADE waveform. Optionally writes the
 *   decoded PCM to stdout. Exit code 0 only if the modem synced AND (when a
 *   callsign was sent) it round-tripped exactly.
 */
#include "zeus_rade.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#  include <io.h>
#  include <fcntl.h>
#endif

static void set_binary_stdio(void)
{
#ifdef _WIN32
    /* Windows opens stdin/stdout in text mode by default, which mangles binary
     * IQ/PCM (a 0x0A byte ends a read early). Force binary mode. */
    _setmode(_fileno(stdin),  _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
#endif
}

/* Decode IQ from stdin -> PCM to stdout (the original harness behaviour). */
static int run_decode(void)
{
    zeus_rade_global_init();
    zeus_rade *z = zeus_rade_open();
    if (!z) { fprintf(stderr, "zeus_rade_open failed\n"); return 1; }

    int nin_max = zeus_rade_nin_max(z);
    int max_pcm = zeus_rade_max_pcm_per_rx(z);
    RADE_COMP *iq  = (RADE_COMP *)malloc(sizeof(RADE_COMP) * (size_t)nin_max);
    short     *pcm = (short *)malloc(sizeof(short) * (size_t)max_pcm);

    long total_pcm = 0;
    int  synced_ticks = 0, ticks = 0, last_snr = 0;
    for (;;) {
        int nin = zeus_rade_nin(z);
        if (nin <= 0 || nin > nin_max) break;
        size_t got = fread(iq, sizeof(RADE_COMP), (size_t)nin, stdin);
        if (got != (size_t)nin) break;
        int n = zeus_rade_rx(z, iq, pcm);
        if (n > 0) { fwrite(pcm, sizeof(short), (size_t)n, stdout); total_pcm += n; }
        ticks++;
        if (zeus_rade_sync(z)) { synced_ticks++; last_snr = zeus_rade_snr_db(z); }
    }

    char cs[RADE_EOO_CALLSIGN_MAX + 1];
    int csn = zeus_rade_get_eoo_callsign(z, cs);
    fprintf(stderr,
            "zeus_rade_test: pcm_samples=%ld ticks=%d synced=%d last_snr=%ddB callsign=%s\n",
            total_pcm, ticks, synced_ticks, last_snr, csn > 0 ? cs : "(none)");

    free(iq); free(pcm);
    zeus_rade_close(z);
    zeus_rade_global_shutdown();
    return 0;
}

/* Fill `pcm` (16 kHz, int16) with a voiced-ish test signal: a low pitch with a
 * few harmonics and a slow tremolo. Content is irrelevant to modem sync (RADE
 * locks on pilots), but realistic input exercises the LPCNet analyzer properly. */
static void synth_speech(short *pcm, int n, double *phase)
{
    const double f0 = 130.0, fs = 16000.0;
    for (int i = 0; i < n; i++) {
        double t = (*phase += 1.0 / fs);
        double trem = 0.6 + 0.4 * sin(2.0 * M_PI * 3.0 * t);
        double s = 0.0;
        for (int h = 1; h <= 6; h++) s += (1.0 / h) * sin(2.0 * M_PI * f0 * h * t);
        double v = trem * s * 0.18 * 32767.0;
        if (v >  32767.0) v =  32767.0;
        if (v < -32767.0) v = -32767.0;
        pcm[i] = (short)v;
    }
}

/* Encode synthetic speech -> modem (real) -> decode it back. */
static int run_loopback(const char *callsign, int write_pcm)
{
    zeus_rade_global_init();
    zeus_rade *z = zeus_rade_open();
    if (!z) { fprintf(stderr, "zeus_rade_open failed\n"); return 1; }

    int n_speech = zeus_rade_n_speech_samples(z);
    int n_tx_out = zeus_rade_n_tx_out(z);
    int n_eoo    = zeus_rade_n_tx_eoo_out(z);
    if (n_speech <= 0 || n_tx_out <= 0) {
        fprintf(stderr, "loopback: TX surface unavailable (n_speech=%d n_tx_out=%d)\n",
                n_speech, n_tx_out);
        zeus_rade_close(z); zeus_rade_global_shutdown(); return 1;
    }

    if (callsign && callsign[0]) zeus_rade_set_tx_callsign(z, callsign);

    /* Target ~6 s of speech so the RX has time to acquire and the EOO lands. */
    int calls = (6 * 16000) / n_speech;
    if (calls < 8) calls = 8;
    int nin_max = zeus_rade_nin_max(z);
    long tail = (long)nin_max * 8; /* zero pad so the FULL EOO frame is decoded */
    long modem_cap = (long)calls * n_tx_out + n_eoo + tail + 16;
    float *modem = (float *)malloc(sizeof(float) * (size_t)modem_cap); /* real @8k */
    short *speech = (short *)malloc(sizeof(short) * (size_t)n_speech);
    RADE_COMP *txc = (RADE_COMP *)malloc(sizeof(RADE_COMP) * (size_t)(n_tx_out > n_eoo ? n_tx_out : n_eoo));
    long mlen = 0;
    double phase = 0.0;

    for (int c = 0; c < calls; c++) {
        synth_speech(speech, n_speech, &phase);
        int n = zeus_rade_tx(z, speech, txc);
        for (int i = 0; i < n; i++) modem[mlen++] = txc[i].real;
    }
    /* End-of-Over frame (carries the callsign). */
    int ne = zeus_rade_tx_eoo(z, txc);
    for (int i = 0; i < ne; i++) modem[mlen++] = txc[i].real;
    for (long i = 0; i < tail; i++) modem[mlen++] = 0.0f; /* flush the EOO through */
    fprintf(stderr, "loopback: encoded %ld modem samples @8k (%d calls + %d eoo + %ld pad)\n",
            mlen, calls, ne, tail);

    /* Decode the encoded modem stream back through the same shim. */
    int max_pcm = zeus_rade_max_pcm_per_rx(z);
    RADE_COMP *iq  = (RADE_COMP *)malloc(sizeof(RADE_COMP) * (size_t)nin_max);
    short     *pcm = (short *)malloc(sizeof(short) * (size_t)max_pcm);
    long total_pcm = 0; int synced_ticks = 0, ticks = 0, last_snr = 0;
    long pos = 0;
    for (;;) {
        int nin = zeus_rade_nin(z);
        if (nin <= 0 || nin > nin_max) break;
        if (pos + nin > mlen) break;
        for (int i = 0; i < nin; i++) { iq[i].real = modem[pos + i]; iq[i].imag = 0.0f; }
        pos += nin;
        int n = zeus_rade_rx(z, iq, pcm);
        if (n > 0) {
            total_pcm += n;
            if (write_pcm) fwrite(pcm, sizeof(short), (size_t)n, stdout);
        }
        ticks++;
        if (zeus_rade_sync(z)) { synced_ticks++; last_snr = zeus_rade_snr_db(z); }
    }

    char cs[RADE_EOO_CALLSIGN_MAX + 1];
    int csn = zeus_rade_get_eoo_callsign(z, cs);
    fprintf(stderr,
            "loopback: ticks=%d synced=%d last_snr=%ddB pcm=%ld callsign=%s\n",
            ticks, synced_ticks, last_snr, total_pcm, csn > 0 ? cs : "(none)");

    int ok = (synced_ticks > 0);
    if (callsign && callsign[0]) {
        int match = (csn > 0 && strcmp(cs, callsign) == 0);
        if (!match) { fprintf(stderr, "loopback: FAIL callsign '%s' != '%s'\n", csn > 0 ? cs : "(none)", callsign); ok = 0; }
        else fprintf(stderr, "loopback: callsign round-trip OK ('%s')\n", cs);
    }
    if (!ok) fprintf(stderr, "loopback: FAIL (synced=%d)\n", synced_ticks);
    else     fprintf(stderr, "loopback: PASS\n");

    free(modem); free(speech); free(txc); free(iq); free(pcm);
    zeus_rade_close(z);
    zeus_rade_global_shutdown();
    return ok ? 0 : 2;
}

int main(int argc, char **argv)
{
    set_binary_stdio();
    if (argc >= 2 && strcmp(argv[1], "loopback") == 0) {
        const char *cs = (argc >= 3) ? argv[2] : "";
        /* Write decoded PCM to stdout only if a 3rd/4th arg "pcm" is given, so the
         * default run is quiet (stderr report only). */
        int write_pcm = (argc >= 4 && strcmp(argv[3], "pcm") == 0);
        return run_loopback(cs, write_pcm);
    }
    return run_decode();
}

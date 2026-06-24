/* SPDX-License-Identifier: BSD-2-Clause
 *
 * zeus_rade_test — validates the zeus_rade shim end-to-end against a real
 * off-air RADE recording. Reads interleaved complex IQ (RADE_COMP) @ 8 kHz from
 * stdin, decodes via the single shim API, writes int16 PCM @ 16 kHz to stdout.
 *
 *   sox FDV_offair.wav -r 8000 -e float -b 32 -c 1 -t raw - \
 *     | real2iq | zeus_rade_test > out.pcm
 *   sox -t .s16 -r 16000 -c 1 out.pcm decoded.wav   # listen / measure
 */
#include "zeus_rade.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int main(void)
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

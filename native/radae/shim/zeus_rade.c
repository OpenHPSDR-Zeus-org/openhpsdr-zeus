/* SPDX-License-Identifier: BSD-2-Clause
 *
 * zeus_rade — single-entry RADE V1 decode shim (see zeus_rade.h).
 *
 * Combines radae_nopy's `rade` modem (IQ -> feature frames) with Opus's FARGAN
 * vocoder (feature frames -> 16 kHz PCM) into one streaming call. Mirrors the
 * verified reference flow from radae_nopy's lpcnet_demo.c (-fargan-synthesis):
 * FARGAN is primed with the first 5 feature frames, then synthesizes 160 PCM
 * samples per subsequent frame. RADE + FARGAN weights are compiled in.
 *
 * Frame constants (from the Opus DNN build): a feature frame is
 * NB_TOTAL_FEATURES(36) floats; FARGAN consumes the first NB_FEATURES(20) and
 * emits LPCNET_FRAME_SIZE(160) samples at 16 kHz.
 */
#include "zeus_rade.h"

#include <stdlib.h>
#include <string.h>
#include <math.h>

/* Opus FARGAN vocoder. */
#include "fargan.h"
#include "freq.h"      /* NB_TOTAL_FEATURES, NB_FEATURES */
#include "lpcnet.h"    /* LPCNET_FRAME_SIZE */

#define ZR_PRIME_FRAMES 5
/* A RADE modem frame yields a bounded number of feature frames; this caps the
 * PCM produced per rx call. Generous — guards the output buffer against any
 * upstream change without truncating real output. */
#define ZR_MAX_FRAMES_PER_RX 64

struct zeus_rade {
    struct rade *r;
    FARGANState  fargan;
    int   n_features;     /* rade_n_features_in_out (feature floats per RADE frame group) */
    int   n_eoo_bits;
    int   primed;         /* FARGAN continuation done */
    int   prime_have;     /* feature frames collected toward priming */
    float prime_buf[ZR_PRIME_FRAMES * NB_TOTAL_FEATURES];
    float feat_scratch[ZR_MAX_FRAMES_PER_RX * NB_TOTAL_FEATURES];
    float eoo_scratch[1024];
    int   have_callsign;
    char  callsign[RADE_EOO_CALLSIGN_MAX + 1];
};

void zeus_rade_global_init(void)     { rade_initialize(); }
void zeus_rade_global_shutdown(void) { rade_finalize(); }

zeus_rade *zeus_rade_open(void)
{
    zeus_rade *z = (zeus_rade *)calloc(1, sizeof(*z));
    if (!z) return NULL;

    /* radae_nopy compiles the weights in, so the model path is unused; the C
     * encoder/decoder paths are what make this Python-free. */
    z->r = rade_open("", RADE_USE_C_ENCODER | RADE_USE_C_DECODER | RADE_VERBOSE_0);
    if (!z->r) { free(z); return NULL; }

    z->n_features  = rade_n_features_in_out(z->r);
    z->n_eoo_bits  = rade_n_eoo_bits(z->r);
    fargan_init(&z->fargan);
    z->primed = 0;
    z->prime_have = 0;
    z->have_callsign = 0;
    z->callsign[0] = '\0';
    return z;
}

void zeus_rade_close(zeus_rade *z)
{
    if (!z) return;
    if (z->r) rade_close(z->r);
    free(z);
}

int zeus_rade_nin(zeus_rade *z)            { return rade_nin(z->r); }
int zeus_rade_nin_max(zeus_rade *z)        { return rade_nin_max(z->r); }
int zeus_rade_max_pcm_per_rx(zeus_rade *z) { (void)z; return ZR_MAX_FRAMES_PER_RX * LPCNET_FRAME_SIZE; }
int zeus_rade_sync(zeus_rade *z)           { return rade_sync(z->r); }
float zeus_rade_freq_offset(zeus_rade *z)  { return rade_freq_offset(z->r); }
int zeus_rade_snr_db(zeus_rade *z)         { return rade_snrdB_3k_est(z->r); }

int zeus_rade_get_eoo_callsign(zeus_rade *z, char *callsign_out)
{
    if (!z->have_callsign) { if (callsign_out) callsign_out[0] = '\0'; return 0; }
    int n = (int)strlen(z->callsign);
    if (callsign_out) memcpy(callsign_out, z->callsign, (size_t)n + 1);
    z->have_callsign = 0; /* consume */
    return n;
}

/* Prime FARGAN exactly as the reference does: 5 frames of NB_TOTAL_FEATURES read
 * into NB_FEATURES-strided slots, with 2 frames of zero PCM history. */
static void zr_try_prime(zeus_rade *z, const float *frame36)
{
    memcpy(&z->prime_buf[z->prime_have * NB_FEATURES], frame36,
           NB_TOTAL_FEATURES * sizeof(float));
    z->prime_have++;
    if (z->prime_have == ZR_PRIME_FRAMES) {
        float zeros[2 * LPCNET_FRAME_SIZE];
        memset(zeros, 0, sizeof(zeros));
        fargan_cont(&z->fargan, zeros, z->prime_buf);
        z->primed = 1;
    }
}

int zeus_rade_rx(zeus_rade *z, const RADE_COMP *rx_in, short *pcm_out)
{
    int has_eoo = 0;
    int nfloats = rade_rx(z->r, z->feat_scratch, &has_eoo, z->eoo_scratch,
                          (RADE_COMP *)rx_in);

    /* On loss of sync, drop the priming so we re-prime cleanly on re-acquire. */
    if (!rade_sync(z->r)) { z->primed = 0; z->prime_have = 0; }

    if (has_eoo) {
        char cs[RADE_EOO_CALLSIGN_MAX + 1];
        int n = rade_rx_get_eoo_callsign(z->eoo_scratch, z->n_eoo_bits, cs);
        if (n > 0) { memcpy(z->callsign, cs, (size_t)n + 1); z->have_callsign = 1; }
    }

    if (nfloats <= 0) return 0;

    int n_pcm = 0;
    int frames = nfloats / NB_TOTAL_FEATURES;
    if (frames > ZR_MAX_FRAMES_PER_RX) frames = ZR_MAX_FRAMES_PER_RX;
    for (int f = 0; f < frames; f++) {
        const float *frame36 = &z->feat_scratch[f * NB_TOTAL_FEATURES];
        if (!z->primed) {
            zr_try_prime(z, frame36);
            continue; /* priming frames produce no audio (matches reference) */
        }
        float feats[NB_FEATURES];
        float fpcm[LPCNET_FRAME_SIZE];
        memcpy(feats, frame36, NB_FEATURES * sizeof(float)); /* OPUS_COPY first 20 */
        fargan_synthesize(&z->fargan, fpcm, feats);
        for (int i = 0; i < LPCNET_FRAME_SIZE; i++) {
            float s = 32768.0f * fpcm[i];
            if (s >  32767.0f) s =  32767.0f;
            if (s < -32767.0f) s = -32767.0f;
            pcm_out[n_pcm++] = (short)floorf(0.5f + s);
        }
    }
    return n_pcm;
}

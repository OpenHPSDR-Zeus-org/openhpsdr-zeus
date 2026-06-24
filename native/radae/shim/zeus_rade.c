/* SPDX-License-Identifier: BSD-2-Clause
 *
 * zeus_rade — single-entry RADE V1 shim (see zeus_rade.h).
 *
 * RX: radae_c's `rade` modem (IQ -> feature frames) + Opus's FARGAN vocoder
 * (feature frames -> 16 kHz PCM). FARGAN is primed with the first 5 feature
 * frames, then synthesizes 160 PCM samples per subsequent frame.
 *
 * TX: Opus's LPCNet feature analyzer (16 kHz speech -> 36-float frames) +
 * radae_c's `rade_tx` (feature frames -> modem IQ @ 8 kHz). The real part of the
 * modem IQ is the transmitted SSB audio (the inverse of the RX feed). The
 * End-of-Over callsign is carried by the FreeDV reliable-text LDPC (rade_text),
 * matching FreeDV-GUI on the air.
 *
 * RADE + FARGAN weights and the LPCNet feature analyzer are all compiled in
 * (Opus DEEP_PLC build) — no model files at runtime.
 *
 * Frame constants (from the Opus DNN build): a feature frame is
 * NB_TOTAL_FEATURES(36) floats; FARGAN consumes the first NB_FEATURES(20) and
 * emits LPCNET_FRAME_SIZE(160) samples at 16 kHz, and the LPCNet analyzer
 * consumes LPCNET_FRAME_SIZE samples per 36-float feature frame.
 */
#include "zeus_rade.h"

#include <stdlib.h>
#include <string.h>
#include <math.h>

/* Opus FARGAN vocoder + LPCNet feature analyzer. */
#include "fargan.h"
#include "freq.h"      /* NB_TOTAL_FEATURES, NB_FEATURES */
#include "lpcnet.h"    /* LPCNET_FRAME_SIZE, LPCNetEncState, lpcnet_compute_single_frame_features */

/* FreeDV reliable-text (LDPC) EOO callsign codec — on-air compatible with
 * FreeDV-GUI. Replaces radae_c's internal 7-bit packing for the callsign. */
#include "rade_text.h"

#define ZR_PRIME_FRAMES 5
/* A RADE modem frame yields a bounded number of feature frames; this caps the
 * PCM produced per rx call. Generous — guards the output buffer against any
 * upstream change without truncating real output. */
#define ZR_MAX_FRAMES_PER_RX 64

/* OPUS_DISABLE_INTRINSICS build: the runtime CPU-dispatch arch is the scalar
 * path (0). The whole library is built scalar/portable, so 0 is correct on every
 * target; a future SIMD build would pass opus_select_arch() here instead. */
#define ZR_ARCH 0

struct zeus_rade {
    struct rade *r;

    /* ---- RX (decode) ---- */
    FARGANState  fargan;
    int   n_features;     /* rade_n_features_in_out (feature floats per RADE frame group) */
    int   n_eoo_bits;
    int   primed;         /* FARGAN continuation done */
    int   prime_have;     /* feature frames collected toward priming */
    float prime_buf[ZR_PRIME_FRAMES * NB_TOTAL_FEATURES];
    float feat_scratch[ZR_MAX_FRAMES_PER_RX * NB_TOTAL_FEATURES];
    float eoo_scratch[1024];
    rade_text_t  txt_rx;  /* reliable-text decoder; fires zr_on_text_rx */
    int   have_callsign;
    char  callsign[RADE_EOO_CALLSIGN_MAX + 1];

    /* ---- TX (encode) ---- */
    LPCNetEncState *enc;  /* LPCNet feature analyzer (speech -> features) */
    int   n_speech;       /* int16 @16k consumed per zeus_rade_tx call */
    int   n_tx_out;       /* RADE_COMP produced per zeus_rade_tx call */
    int   n_tx_eoo_out;   /* RADE_COMP in the EOO frame */
    float tx_feat[ZR_MAX_FRAMES_PER_RX * NB_TOTAL_FEATURES]; /* features for one tx call */
    rade_text_t  txt_tx;  /* reliable-text encoder for the EOO callsign */
    float eoo_tx_bits[1024]; /* EOO symbol payload handed to rade_tx_set_eoo_bits */
};

/* rade_text RX callback: a full, CRC-validated callsign was decoded. */
static void zr_on_text_rx(rade_text_t rt, const char *txt, int length, void *state)
{
    (void)rt;
    struct zeus_rade *z = (struct zeus_rade *)state;
    if (!z || length <= 0) return;
    int n = length < RADE_EOO_CALLSIGN_MAX ? length : RADE_EOO_CALLSIGN_MAX;
    memcpy(z->callsign, txt, (size_t)n);
    z->callsign[n] = '\0';
    z->have_callsign = 1;
}

void zeus_rade_global_init(void)     { rade_initialize(); }
void zeus_rade_global_shutdown(void) { rade_finalize(); }

zeus_rade *zeus_rade_open(void)
{
    zeus_rade *z = (zeus_rade *)calloc(1, sizeof(*z));
    if (!z) return NULL;

    /* radae_c compiles the weights in, so the model path is unused; the C
     * encoder/decoder paths are what make this Python-free. */
    z->r = rade_open("", RADE_USE_C_ENCODER | RADE_USE_C_DECODER | RADE_VERBOSE_0);
    if (!z->r) { free(z); return NULL; }

    z->n_features    = rade_n_features_in_out(z->r);
    z->n_eoo_bits    = rade_n_eoo_bits(z->r);
    z->n_tx_out      = rade_n_tx_out(z->r);
    z->n_tx_eoo_out  = rade_n_tx_eoo_out(z->r);
    /* One LPCNet feature frame is NB_TOTAL_FEATURES floats from LPCNET_FRAME_SIZE
     * speech samples; a tx call consumes n_features/NB_TOTAL_FEATURES frames. */
    z->n_speech      = (z->n_features / NB_TOTAL_FEATURES) * LPCNET_FRAME_SIZE;

    fargan_init(&z->fargan);
    z->primed = 0;
    z->prime_have = 0;
    z->have_callsign = 0;
    z->callsign[0] = '\0';

    /* LPCNet feature analyzer for TX. Pure DSP + compiled-in pitch DNN — no model
     * file. create() allocates; init() resets streaming state. */
    z->enc = lpcnet_encoder_create();
    if (z->enc) lpcnet_encoder_init(z->enc);

    /* Reliable-text EOO codecs (RX decode + TX encode). */
    z->txt_rx = rade_text_create();
    if (z->txt_rx) rade_text_set_rx_callback(z->txt_rx, zr_on_text_rx, z);
    z->txt_tx = rade_text_create();

    return z;
}

void zeus_rade_close(zeus_rade *z)
{
    if (!z) return;
    if (z->txt_rx) rade_text_destroy(z->txt_rx);
    if (z->txt_tx) rade_text_destroy(z->txt_tx);
    if (z->enc) lpcnet_encoder_destroy(z->enc);
    if (z->r) rade_close(z->r);
    free(z);
}

int zeus_rade_nin(zeus_rade *z)            { return rade_nin(z->r); }
int zeus_rade_nin_max(zeus_rade *z)        { return rade_nin_max(z->r); }
int zeus_rade_max_pcm_per_rx(zeus_rade *z) { (void)z; return ZR_MAX_FRAMES_PER_RX * LPCNET_FRAME_SIZE; }
int zeus_rade_sync(zeus_rade *z)           { return rade_sync(z->r); }
float zeus_rade_freq_offset(zeus_rade *z)  { return rade_freq_offset(z->r); }
int zeus_rade_snr_db(zeus_rade *z)         { return rade_snrdB_3k_est(z->r); }

int zeus_rade_n_speech_samples(zeus_rade *z) { return z->n_speech; }
int zeus_rade_n_tx_out(zeus_rade *z)         { return z->n_tx_out; }
int zeus_rade_n_tx_eoo_out(zeus_rade *z)     { return z->n_tx_eoo_out; }

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

    /* End-of-Over: decode the callsign with the reliable-text LDPC (CRC-checked).
     * zr_on_text_rx fires from inside rade_text_rx when a valid block lands. */
    if (has_eoo && z->txt_rx) {
        rade_text_rx(z->txt_rx, z->eoo_scratch, z->n_eoo_bits);
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

/* ---- Transmit ---------------------------------------------------------- */

int zeus_rade_tx(zeus_rade *z, const short *pcm_in, RADE_COMP *tx_out)
{
    if (!z->enc) return 0;
    int frames = z->n_features / NB_TOTAL_FEATURES;
    if (frames > ZR_MAX_FRAMES_PER_RX) frames = ZR_MAX_FRAMES_PER_RX;
    /* Analyze each 160-sample speech frame into 36 features, contiguously, so the
     * block matches rade_tx's expected n_features layout. */
    for (int f = 0; f < frames; f++) {
        lpcnet_compute_single_frame_features(
            z->enc, pcm_in + (size_t)f * LPCNET_FRAME_SIZE,
            &z->tx_feat[f * NB_TOTAL_FEATURES], ZR_ARCH);
    }
    return rade_tx(z->r, tx_out, z->tx_feat);
}

int zeus_rade_tx_eoo(zeus_rade *z, RADE_COMP *tx_eoo_out)
{
    return rade_tx_eoo(z->r, tx_eoo_out);
}

void zeus_rade_set_tx_callsign(zeus_rade *z, const char *callsign)
{
    if (!z->txt_tx) return;
    int n = z->n_eoo_bits;
    if (n > (int)(sizeof(z->eoo_tx_bits) / sizeof(z->eoo_tx_bits[0])))
        n = (int)(sizeof(z->eoo_tx_bits) / sizeof(z->eoo_tx_bits[0]));
    /* The reliable-text block lives at the front of the EOO payload (offset 0);
     * the remainder defaults to zero — matches FreeDV-GUI's EOO layout. */
    memset(z->eoo_tx_bits, 0, sizeof(float) * (size_t)n);
    if (callsign && callsign[0]) {
        rade_text_generate_tx_string(z->txt_tx, callsign,
                                     (int)strlen(callsign), z->eoo_tx_bits, n);
    }
    rade_tx_set_eoo_bits(z->r, z->eoo_tx_bits);
}

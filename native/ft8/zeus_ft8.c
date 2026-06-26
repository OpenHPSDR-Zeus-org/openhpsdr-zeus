// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// zeus_ft8 — stable C ABI over kgoba/ft8_lib (MIT). See zeus_ft8.h for the
// design rationale (per-RX context, thread safety, ABI stability).
//
// This wraps the vendored ft8_lib decode/encode pipeline exactly as the
// upstream demo does (monitor -> waterfall -> find_candidates -> decode ->
// dedup -> unpack), but with all per-receiver state moved into a caller-owned
// context so multiple RX slices can decode concurrently.

#include "zeus_ft8.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifndef _USE_MATH_DEFINES
#define _USE_MATH_DEFINES // MSVC: expose M_PI from <math.h>
#endif
#include <math.h>
#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

#include "ft8/decode.h"
#include "ft8/encode.h"
#include "ft8/message.h"
#include "ft8/constants.h"
#include "common/monitor.h"

// Decode tuning — matches the upstream demo defaults so single-pass output is
// bit-for-bit comparable to the reference decoder during verification.
#define ZF_MIN_SCORE        10
#define ZF_MAX_CANDIDATES   140
#define ZF_LDPC_ITERATIONS  25
#define ZF_MAX_DECODED      64    // >= ft8_lib demo's 50; covers a busy slot
#define ZF_FREQ_OSR         2     // freq_osr=4 measured WORSE on the corpus; keep 2
#define ZF_TIME_OSR         4     // finer time sync: +10 decodes vs osr=2 (measured)
#define ZF_CALLSIGN_HT_SIZE 256

// ---- per-RX context -------------------------------------------------------

struct zeus_ft8_ctx
{
    struct
    {
        char     callsign[12]; // up to 11 chars + NUL
        uint32_t hash;         // 8 MSB = age, 22 LSB = hash
    } ht[ZF_CALLSIGN_HT_SIZE];
    int ht_size;
};

// The ft8_lib callsign-hash interface carries no user-data pointer, so we
// publish the active context per-thread for the duration of a decode call.
// One context is never driven from two threads at once (one worker per RX),
// so a thread-local is sufficient and lock-free.
#if defined(_MSC_VER)
#define ZF_THREAD_LOCAL __declspec(thread)
#else
#define ZF_THREAD_LOCAL _Thread_local
#endif
static ZF_THREAD_LOCAL zeus_ft8_ctx* zf_active = NULL;

static void zf_ht_add(const char* callsign, uint32_t hash)
{
    zeus_ft8_ctx* c = zf_active;
    if (c == NULL) return;
    uint16_t hash10 = (hash >> 12) & 0x3FFu;
    int idx = (hash10 * 23) % ZF_CALLSIGN_HT_SIZE;
    while (c->ht[idx].callsign[0] != '\0')
    {
        if (((c->ht[idx].hash & 0x3FFFFFu) == hash) && (0 == strcmp(c->ht[idx].callsign, callsign)))
        {
            c->ht[idx].hash &= 0x3FFFFFu; // reset age
            return;
        }
        idx = (idx + 1) % ZF_CALLSIGN_HT_SIZE;
    }
    c->ht_size++;
    strncpy(c->ht[idx].callsign, callsign, 11);
    c->ht[idx].callsign[11] = '\0';
    c->ht[idx].hash = hash;
}

static bool zf_ht_lookup(ftx_callsign_hash_type_t hash_type, uint32_t hash, char* callsign)
{
    zeus_ft8_ctx* c = zf_active;
    if (c == NULL) { callsign[0] = '\0'; return false; }
    uint8_t shift = (hash_type == FTX_CALLSIGN_HASH_10_BITS) ? 12
                  : (hash_type == FTX_CALLSIGN_HASH_12_BITS) ? 10 : 0;
    uint16_t hash10 = (hash >> (12 - shift)) & 0x3FFu;
    int idx = (hash10 * 23) % ZF_CALLSIGN_HT_SIZE;
    while (c->ht[idx].callsign[0] != '\0')
    {
        if (((c->ht[idx].hash & 0x3FFFFFu) >> shift) == hash)
        {
            strcpy(callsign, c->ht[idx].callsign);
            return true;
        }
        idx = (idx + 1) % ZF_CALLSIGN_HT_SIZE;
    }
    callsign[0] = '\0';
    return false;
}

static ftx_callsign_hash_interface_t zf_hash_if = {
    .lookup_hash = zf_ht_lookup,
    .save_hash = zf_ht_add,
};

// ---- lifecycle ------------------------------------------------------------

zeus_ft8_ctx* zeus_ft8_ctx_create(void)
{
    zeus_ft8_ctx* c = (zeus_ft8_ctx*)calloc(1, sizeof(zeus_ft8_ctx));
    return c;
}

void zeus_ft8_ctx_destroy(zeus_ft8_ctx* ctx)
{
    free(ctx);
}

void zeus_ft8_ctx_reset(zeus_ft8_ctx* ctx)
{
    if (ctx == NULL) return;
    memset(ctx->ht, 0, sizeof(ctx->ht));
    ctx->ht_size = 0;
}

// ---- deep decode: subtract-and-redecode -----------------------------------
//
// To unmask weak signals hidden under stronger ones we reconstruct each decoded
// signal's GFSK waveform and subtract it from the slot audio, then re-decode the
// residual. The reconstruction reuses ft8_lib's exact GFSK synthesis math
// (gfsk_pulse + phase accumulation, MIT) split so the carrier frequency is a
// free parameter we can fine-tune — the candidate frequency is only accurate to
// ~1.5 Hz, but a 1 Hz error drifts ~13 cycles over a 12.6 s FT8 frame and ruins
// subtraction, so we grid-search the carrier and fit amplitude+phase by least
// squares before subtracting.

#define ZF_GFSK_K   5.336446f   // == pi*sqrt(2/log(2)), from ft8_lib
#define ZF_FT8_BT   2.0f
// Max samples/symbol we size scratch for: 0.160 s * sample rates up to ~12.5 kHz
// (we resample RX audio to 12 kHz -> 1920 samples/symbol; headroom to 2000).
#define ZF_MAX_SPSYM 2000
#define ZF_MAX_NWAVE (FT8_NN * ZF_MAX_SPSYM)

// One reconstructed signal queued for subtraction after a pass.
typedef struct
{
    uint8_t tones[FT8_NN];
    float   freq_hz;
    float   dt_sec;
} zf_subtractable_t;

// Heap scratch for signal reconstruction (allocated once per deep-decode call,
// not per signal, and only when passes > 1). Avoids multi-MB thread-locals.
typedef struct
{
    float* mphase; // [ZF_MAX_NWAVE]
    float* env;    // [ZF_MAX_NWAVE]
    float* dphi;   // [ZF_MAX_NWAVE + 2*ZF_MAX_SPSYM]
    float* pulse;  // [3*ZF_MAX_SPSYM]
} zf_scratch_t;

static int zf_scratch_alloc(zf_scratch_t* s)
{
    s->mphase = (float*)malloc(sizeof(float) * ZF_MAX_NWAVE);
    s->env    = (float*)malloc(sizeof(float) * ZF_MAX_NWAVE);
    s->dphi   = (float*)malloc(sizeof(float) * (ZF_MAX_NWAVE + 2 * ZF_MAX_SPSYM));
    s->pulse  = (float*)malloc(sizeof(float) * (3 * ZF_MAX_SPSYM));
    if (!s->mphase || !s->env || !s->dphi || !s->pulse) return 0;
    return 1;
}

static void zf_scratch_free(zf_scratch_t* s)
{
    free(s->mphase); free(s->env); free(s->dphi); free(s->pulse);
    s->mphase = s->env = s->dphi = s->pulse = NULL;
}

// Build the modulation phase (carrier-free cumulative phase) and the end-ramp
// envelope for a tone sequence. Returns samples-per-symbol; fills n_wave.
static int zf_build_modphase(const uint8_t* tones, int n_sym, int sample_rate,
                             zf_scratch_t* s, int* n_wave_out)
{
    int n_spsym = (int)(0.5f + sample_rate * FT8_SYMBOL_PERIOD);
    int n_wave = n_sym * n_spsym;
    if (n_spsym > ZF_MAX_SPSYM || n_wave > ZF_MAX_NWAVE) { *n_wave_out = 0; return 0; }

    float dphi_peak = 2.0f * (float)M_PI / n_spsym; // hmod = 1
    int ext = n_wave + 2 * n_spsym;
    float* dphi = s->dphi;
    float* pulse = s->pulse;

    // GFSK smoothing pulse (3 symbols long).
    for (int i = 0; i < 3 * n_spsym; ++i)
    {
        float t = i / (float)n_spsym - 1.5f;
        float a1 = ZF_GFSK_K * ZF_FT8_BT * (t + 0.5f);
        float a2 = ZF_GFSK_K * ZF_FT8_BT * (t - 0.5f);
        pulse[i] = (erff(a1) - erff(a2)) / 2.0f;
    }

    // Carrier-free per-sample phase increment (modulation only).
    for (int i = 0; i < ext; ++i) dphi[i] = 0.0f;
    for (int i = 0; i < n_sym; ++i)
    {
        int ib = i * n_spsym;
        for (int j = 0; j < 3 * n_spsym; ++j)
            dphi[j + ib] += dphi_peak * tones[i] * pulse[j];
    }
    for (int j = 0; j < 2 * n_spsym; ++j)
    {
        dphi[j] += dphi_peak * pulse[j + n_spsym] * tones[0];
        dphi[j + n_sym * n_spsym] += dphi_peak * pulse[j] * tones[n_sym - 1];
    }

    // Cumulative modulation phase used by output sample k is dphi[k+n_spsym].
    float acc = 0.0f;
    for (int k = 0; k < n_wave; ++k)
    {
        s->mphase[k] = acc;
        acc += dphi[k + n_spsym];
    }

    // End-ramp envelope (matches synth_gfsk).
    for (int k = 0; k < n_wave; ++k) s->env[k] = 1.0f;
    int n_ramp = n_spsym / 8;
    for (int i = 0; i < n_ramp; ++i)
    {
        float e = (1.0f - cosf(2.0f * (float)M_PI * i / (2 * n_ramp))) / 2.0f;
        s->env[i] *= e;
        s->env[n_wave - 1 - i] *= e;
    }

    *n_wave_out = n_wave;
    return n_spsym;
}

// Fit amplitude+phase of a reconstructed signal at carrier w (rad/sample) by
// 2D least squares onto cos/sin templates over the audio span, and return the
// explained energy (higher = better subtraction). If `apply`, subtract the fit.
static float zf_fit_subtract(float* audio, int n, int pos0,
                             const float* mphase, const float* env, int n_wave,
                             float w, int apply)
{
    double Scc = 0, Sss = 0, Scs = 0, Rc = 0, Rs = 0;
    int kstart = (pos0 < 0) ? -pos0 : 0;
    for (int k = kstart; k < n_wave; ++k)
    {
        int p = pos0 + k;
        if (p >= n) break;
        float ph = mphase[k] + k * w;
        float c = env[k] * cosf(ph);
        float s = env[k] * sinf(ph);
        float r = audio[p];
        Scc += (double)c * c; Sss += (double)s * s; Scs += (double)c * s;
        Rc += (double)r * c;  Rs += (double)r * s;
    }
    double det = Scc * Sss - Scs * Scs;
    if (det <= 1e-9) return 0.0f;
    double alpha = (Rc * Sss - Rs * Scs) / det;
    double beta  = (Rs * Scc - Rc * Scs) / det;
    double explained = alpha * Rc + beta * Rs;

    if (apply)
    {
        for (int k = kstart; k < n_wave; ++k)
        {
            int p = pos0 + k;
            if (p >= n) break;
            float ph = mphase[k] + k * w;
            audio[p] -= (float)(alpha * env[k] * cosf(ph) + beta * env[k] * sinf(ph));
        }
    }
    return (float)explained;
}

// Reconstruct one decoded FT8 signal and subtract it from the working audio,
// fine-searching the carrier frequency for the cleanest removal.
static void zf_subtract_signal(float* audio, int n, int sample_rate,
                               const zf_subtractable_t* sig, zf_scratch_t* s)
{
    int n_wave = 0;
    zf_build_modphase(sig->tones, FT8_NN, sample_rate, s, &n_wave);
    if (n_wave == 0) return;

    int pos0 = (int)(sig->dt_sec * sample_rate + 0.5f);
    const float two_pi = 2.0f * (float)M_PI;

    // Coarse then fine frequency search for max explained energy.
    float best_f = sig->freq_hz, best_e = -1.0f;
    for (float df = -2.5f; df <= 2.5f; df += 0.25f)
    {
        float f = sig->freq_hz + df;
        float e = zf_fit_subtract(audio, n, pos0, s->mphase, s->env, n_wave, two_pi * f / sample_rate, 0);
        if (e > best_e) { best_e = e; best_f = f; }
    }
    for (float df = -0.20f; df <= 0.20f; df += 0.025f)
    {
        float f = best_f + df;
        float e = zf_fit_subtract(audio, n, pos0, s->mphase, s->env, n_wave, two_pi * f / sample_rate, 0);
        if (e > best_e) { best_e = e; best_f = f; }
    }
    zf_fit_subtract(audio, n, pos0, s->mphase, s->env, n_wave, two_pi * best_f / sample_rate, 1);
    if (getenv("ZF_DEBUG"))
        fprintf(stderr, "[zf]   subtract f=%.1f (cand %.1f) explained=%.3g\n",
                best_f, sig->freq_hz, best_e);
}

// ---- decode ---------------------------------------------------------------

int32_t zeus_ft8_decode(zeus_ft8_ctx* ctx,
                        const float* samples, int32_t n,
                        int32_t sample_rate, int32_t protocol,
                        int32_t passes,
                        zeus_ft8_decode_t* out, int32_t max_results)
{
    if (ctx == NULL || samples == NULL || out == NULL || max_results <= 0)
        return -1;
    if (sample_rate <= 0 || n <= 0)
        return -1;

    ftx_protocol_t proto = (protocol == ZEUS_FT8_PROTO_FT4) ? FTX_PROTOCOL_FT4 : FTX_PROTOCOL_FT8;

    int n_passes = passes < 1 ? 1 : (passes > 4 ? 4 : passes);
    // Subtract-and-redecode is implemented for FT8 only (FT4 ramp symbols +
    // shorter frame need their own synthesis); FT4 runs single-pass for now.
    if (proto == FTX_PROTOCOL_FT4) n_passes = 1;

    int tosr = ZF_TIME_OSR, fosr = ZF_FREQ_OSR, minscore = ZF_MIN_SCORE, ldpcit = ZF_LDPC_ITERATIONS;
    { const char* e;
      if ((e = getenv("ZF_TOSR"))) tosr = atoi(e);
      if ((e = getenv("ZF_FOSR"))) fosr = atoi(e);
      if ((e = getenv("ZF_MINSCORE"))) minscore = atoi(e);
      if ((e = getenv("ZF_LDPCIT"))) ldpcit = atoi(e);
      // Clamp diagnostic overrides to safe ranges (osr feeds FFT sizing).
      if (tosr < 1) tosr = 1; if (tosr > 8) tosr = 8;
      if (fosr < 1) fosr = 1; if (fosr > 8) fosr = 8;
      if (minscore < 0) minscore = 0;
      if (ldpcit < 1) ldpcit = 1; }

    monitor_config_t cfg = {
        .f_min = 200.0f,
        .f_max = 3000.0f,
        .sample_rate = sample_rate,
        .time_osr = tosr,
        .freq_osr = fosr,
        .protocol = proto,
    };

    // Working audio buffer: only copied/mutated when we will subtract (>1 pass).
    float* work = NULL;
    const float* audio = samples;
    zf_scratch_t scratch = {0};
    if (n_passes > 1)
    {
        work = (float*)malloc((size_t)n * sizeof(float));
        if (work != NULL && zf_scratch_alloc(&scratch))
        {
            memcpy(work, samples, (size_t)n * sizeof(float));
            audio = work;
        }
        else
        {
            // Allocation failed — fall back to a safe single pass.
            if (work) { free(work); work = NULL; }
            zf_scratch_free(&scratch);
            n_passes = 1;
        }
    }

    // Dedup table of decoded payloads, persisted across all passes.
    ftx_message_t  decoded[ZF_MAX_DECODED];
    ftx_message_t* decoded_ht[ZF_MAX_DECODED];
    for (int i = 0; i < ZF_MAX_DECODED; ++i) decoded_ht[i] = NULL;
    int num_decoded = 0;

    zf_active = ctx; // publish context for the hash callbacks
    int written = 0;

    for (int pass = 0; pass < n_passes; ++pass)
    {
        monitor_t mon;
        monitor_init(&mon, &cfg);
        for (int frame_pos = 0; frame_pos + mon.block_size <= n; frame_pos += mon.block_size)
            monitor_process(&mon, audio + frame_pos);
        const ftx_waterfall_t* wf = &mon.wf;

        ftx_candidate_t cands[ZF_MAX_CANDIDATES];
        int num_cands = ftx_find_candidates(wf, ZF_MAX_CANDIDATES, cands, minscore);
        if (getenv("ZF_DEBUG"))
            fprintf(stderr, "[zf] pass %d: %d candidates, max score %d\n",
                    pass, num_cands, num_cands > 0 ? cands[0].score : -1);

        // Signals newly decoded this pass, queued for subtraction before next.
        zf_subtractable_t subs[ZF_MAX_DECODED];
        int n_subs = 0;
        int new_this_pass = 0;

        for (int idx = 0; idx < num_cands; ++idx)
        {
            if (num_decoded >= ZF_MAX_DECODED) break; // keep an empty dedup slot

            const ftx_candidate_t* cand = &cands[idx];
            ftx_message_t msg;
            ftx_decode_status_t status;
            if (!ftx_decode_candidate(wf, cand, ldpcit, &msg, &status))
                continue;

            // Dedup by payload hash (open-addressed, same scheme as upstream).
            int h = msg.hash % ZF_MAX_DECODED;
            bool empty = false, dup = false;
            do
            {
                if (decoded_ht[h] == NULL) { empty = true; }
                else if ((decoded_ht[h]->hash == msg.hash) &&
                         (0 == memcmp(decoded_ht[h]->payload, msg.payload, sizeof(msg.payload))))
                { dup = true; }
                else { h = (h + 1) % ZF_MAX_DECODED; }
            } while (!empty && !dup);
            if (dup) continue;

            memcpy(&decoded[h], &msg, sizeof(msg));
            decoded_ht[h] = &decoded[h];
            ++num_decoded;
            ++new_this_pass;

            float freq_hz = (mon.min_bin + cand->freq_offset + (float)cand->freq_sub / wf->freq_osr) / mon.symbol_period;
            float dt_sec = (cand->time_offset + (float)cand->time_sub / wf->time_osr) * mon.symbol_period;

            // Queue for subtraction (reconstruct the full 79-symbol waveform).
            if (work != NULL && n_subs < ZF_MAX_DECODED && pass + 1 < n_passes)
            {
                ft8_encode(msg.payload, subs[n_subs].tones);
                subs[n_subs].freq_hz = freq_hz;
                subs[n_subs].dt_sec = dt_sec;
                ++n_subs;
            }

            // Emit (if the caller's buffer still has room).
            if (written < max_results)
            {
                char text[FTX_MAX_MESSAGE_LENGTH];
                ftx_message_offsets_t offsets;
                if (ftx_message_decode(&msg, &zf_hash_if, text, &offsets) != FTX_MESSAGE_RC_OK)
                    continue; // unpack failed; don't emit garbage

                zeus_ft8_decode_t* o = &out[written++];
                // Approximate SNR from sync score (2500 Hz reference). Proper
                // noise-floor SNR estimation is a follow-up refinement.
                o->snr_db = (float)cand->score * 0.5f - 24.0f;
                o->dt_sec = dt_sec;
                o->freq_hz = freq_hz;
                o->score = cand->score;
                o->ldpc_errors = status.ldpc_errors;
                strncpy(o->text, text, sizeof(o->text) - 1);
                o->text[sizeof(o->text) - 1] = '\0';
            }
        }

        monitor_free(&mon);

        // Converged (no new decodes) or last pass — stop.
        if (new_this_pass == 0 || pass + 1 >= n_passes) break;

        // Subtract this pass's decodes from the working audio, then re-decode.
        for (int i = 0; i < n_subs; ++i)
            zf_subtract_signal(work, n, sample_rate, &subs[i], &scratch);
    }

    zf_active = NULL;
    if (work) free(work);
    zf_scratch_free(&scratch);
    return written;
}

// ---- encode ---------------------------------------------------------------

int32_t zeus_ft8_encode(const char* message, int32_t protocol,
                        uint8_t* tones, int32_t max_tones)
{
    if (message == NULL || tones == NULL) return -1;
    ftx_protocol_t proto = (protocol == ZEUS_FT8_PROTO_FT4) ? FTX_PROTOCOL_FT4 : FTX_PROTOCOL_FT8;
    int nn = (proto == FTX_PROTOCOL_FT4) ? FT4_NN : FT8_NN;
    if (max_tones < nn) return -2;

    ftx_message_t msg;
    ftx_message_init(&msg);
    ftx_message_rc_t rc = ftx_message_encode(&msg, NULL, message);
    if (rc != FTX_MESSAGE_RC_OK) return -3;

    if (proto == FTX_PROTOCOL_FT4)
        ft4_encode(msg.payload, tones);
    else
        ft8_encode(msg.payload, tones);

    return nn;
}

const char* zeus_ft8_version(void)
{
    return "zeus_ft8 0.2 (ft8_lib MIT, multi-pass subtract-and-redecode)";
}

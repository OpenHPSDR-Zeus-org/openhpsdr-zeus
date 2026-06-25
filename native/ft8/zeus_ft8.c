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
#define ZF_FREQ_OSR         2
#define ZF_TIME_OSR         2
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

    monitor_config_t cfg = {
        .f_min = 200.0f,
        .f_max = 3000.0f,
        .sample_rate = sample_rate,
        .time_osr = ZF_TIME_OSR,
        .freq_osr = ZF_FREQ_OSR,
        .protocol = proto,
    };

    monitor_t mon;
    monitor_init(&mon, &cfg);

    // Accumulate the whole slot into the waterfall, block by block.
    for (int frame_pos = 0; frame_pos + mon.block_size <= n; frame_pos += mon.block_size)
    {
        monitor_process(&mon, samples + frame_pos);
    }

    const ftx_waterfall_t* wf = &mon.wf;

    // Dedup table of decoded payloads for this slot.
    ftx_message_t  decoded[ZF_MAX_DECODED];
    ftx_message_t* decoded_ht[ZF_MAX_DECODED];
    for (int i = 0; i < ZF_MAX_DECODED; ++i) decoded_ht[i] = NULL;
    int num_decoded = 0;

    zf_active = ctx; // publish context for the hash callbacks

    // NOTE: passes > 1 (subtract-and-redecode deep decode) is not yet wired —
    // it requires time-domain resynthesis + subtraction of decoded signals.
    // Single pass for now; the loop structure is in place for the deep-decode
    // follow-up. Re-running find/decode without subtraction yields no new
    // messages, so we run exactly one pass here regardless of `passes`.
    (void)passes;

    ftx_candidate_t cands[ZF_MAX_CANDIDATES];
    int num_cands = ftx_find_candidates(wf, ZF_MAX_CANDIDATES, cands, ZF_MIN_SCORE);

    int written = 0;
    for (int idx = 0; idx < num_cands && written < max_results; ++idx)
    {
        const ftx_candidate_t* cand = &cands[idx];

        ftx_message_t msg;
        ftx_decode_status_t status;
        if (!ftx_decode_candidate(wf, cand, ZF_LDPC_ITERATIONS, &msg, &status))
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

        if (dup || !empty) continue;
        if (num_decoded >= ZF_MAX_DECODED) break;

        memcpy(&decoded[h], &msg, sizeof(msg));
        decoded_ht[h] = &decoded[h];
        ++num_decoded;

        float freq_hz = (mon.min_bin + cand->freq_offset + (float)cand->freq_sub / wf->freq_osr) / mon.symbol_period;
        float dt_sec = (cand->time_offset + (float)cand->time_sub / wf->time_osr) * mon.symbol_period;

        char text[FTX_MAX_MESSAGE_LENGTH];
        ftx_message_offsets_t offsets;
        ftx_message_rc_t rc = ftx_message_decode(&msg, &zf_hash_if, text, &offsets);
        if (rc != FTX_MESSAGE_RC_OK)
            continue; // could not unpack; skip rather than emit garbage

        zeus_ft8_decode_t* o = &out[written++];
        // Approximate SNR from sync score (2500 Hz reference). Proper SNR
        // estimation against the slot noise floor is a follow-up refinement.
        o->snr_db = (float)cand->score * 0.5f - 24.0f;
        o->dt_sec = dt_sec;
        o->freq_hz = freq_hz;
        o->score = cand->score;
        o->ldpc_errors = status.ldpc_errors;
        strncpy(o->text, text, sizeof(o->text) - 1);
        o->text[sizeof(o->text) - 1] = '\0';
    }

    zf_active = NULL;
    monitor_free(&mon);
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
    return "zeus_ft8 0.1 (ft8_lib MIT, single-pass)";
}

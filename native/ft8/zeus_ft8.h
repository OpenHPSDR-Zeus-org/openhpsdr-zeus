// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// zeus_ft8 — a thin, stable C ABI over kgoba/ft8_lib (MIT, see vendor/LICENSE)
// exposing FT8/FT4 slot decode + message encode for P/Invoke from Zeus.Dsp.Ft8.
//
// Design notes:
//   * NO global mutable state. All per-receiver state (the callsign hash table
//     used to resolve <...> hashed calls) lives in an opaque zeus_ft8_ctx that
//     the managed side creates one-per-RX. This makes concurrent decode on
//     multiple RX slices (multi-band) safe: each thread drives its own ctx.
//   * The vendored ft8_lib callsign-hash interface has no user-data pointer, so
//     the active ctx is published in a _Thread_local before each decode and the
//     hash callbacks read it. One ctx must not be driven from two threads at
//     once (it never is — one decode worker per RX).
//   * The ABI is plain C structs by value / flat arrays only — no ft8_lib types
//     leak across the boundary, so the vendored lib can be updated without
//     breaking the managed P/Invoke signatures.

#ifndef ZEUS_FT8_H
#define ZEUS_FT8_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define ZEUS_FT8_API __declspec(dllexport)
#else
#define ZEUS_FT8_API __attribute__((visibility("default")))
#endif

// Protocol selector (matches ft8_lib ftx_protocol_t ordering but kept separate
// so the ABI does not depend on the vendored enum).
#define ZEUS_FT8_PROTO_FT4 0
#define ZEUS_FT8_PROTO_FT8 1

// One decoded message. Mirrors a WSJT-X decode line.
typedef struct
{
    float   snr_db;     // estimated SNR in dB (2500 Hz reference), approximate
    float   dt_sec;     // time offset of the message start, seconds
    float   freq_hz;    // audio frequency, Hz
    int32_t score;      // Costas sync score (higher = stronger sync)
    int32_t ldpc_errors;// residual LDPC errors after decode (0 = clean)
    char    text[40];   // null-terminated decoded message text
} zeus_ft8_decode_t;

// Opaque per-RX decoder context. Create one per receiver slice.
typedef struct zeus_ft8_ctx zeus_ft8_ctx;

// Create / destroy a per-RX context. Returns NULL on allocation failure.
ZEUS_FT8_API zeus_ft8_ctx* zeus_ft8_ctx_create(void);
ZEUS_FT8_API void          zeus_ft8_ctx_destroy(zeus_ft8_ctx* ctx);

// Clear the accumulated callsign hash table (e.g. on band change). Optional.
ZEUS_FT8_API void          zeus_ft8_ctx_reset(zeus_ft8_ctx* ctx);

// Decode one slot of real audio.
//   ctx          per-RX context (must not be shared across threads concurrently)
//   samples      mono float PCM in [-1,1], length n
//   n            number of samples (one 15 s FT8 / 7.5 s FT4 slot at sample_rate)
//   sample_rate  Hz (12000 is the canonical FT8 rate)
//   protocol     ZEUS_FT8_PROTO_FT8 or _FT4
//   passes       subtract-and-redecode passes (1 = stock single pass;
//                >1 enables deep decode of signals masked by stronger ones)
//   out          caller-allocated array of decodes
//   max_results  capacity of out
// Returns the number of decodes written (>=0), or <0 on error.
ZEUS_FT8_API int32_t zeus_ft8_decode(zeus_ft8_ctx* ctx,
                                     const float* samples, int32_t n,
                                     int32_t sample_rate, int32_t protocol,
                                     int32_t passes,
                                     zeus_ft8_decode_t* out, int32_t max_results);

// Encode a message into a tone sequence (FSK tone indices, 0..7 FT8 / 0..3 FT4).
//   message    text, e.g. "CQ KB2UKA FN12"
//   protocol   ZEUS_FT8_PROTO_FT8 (79 tones) or _FT4 (105 tones)
//   tones      caller-allocated output, length >= max_tones
//   max_tones  capacity
// Returns number of tones written, or <0 on error.
ZEUS_FT8_API int32_t zeus_ft8_encode(const char* message, int32_t protocol,
                                     uint8_t* tones, int32_t max_tones);

// Library version string (for diagnostics / the FT8 panel "about").
ZEUS_FT8_API const char* zeus_ft8_version(void);

#ifdef __cplusplus
}
#endif

#endif // ZEUS_FT8_H

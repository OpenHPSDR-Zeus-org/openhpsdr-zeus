// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// zeus_wspr — stable C ABI over the vendored K1JT/K9AN wsprd (GPL-3, see
// vendor/). Exposes WSPR encode now; decode follows once wsprd.c's CLI main is
// refactored into a callable slot-decode entry. The ABI passes only flat C
// types so the managed P/Invoke stays stable across re-vendoring.

#ifndef ZEUS_WSPR_H
#define ZEUS_WSPR_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define ZEUS_WSPR_API __declspec(dllexport)
#else
#define ZEUS_WSPR_API __attribute__((visibility("default")))
#endif

// Number of channel symbols in a WSPR transmission.
#define ZEUS_WSPR_NSYM 162

// Encode a WSPR message into 162 4-FSK channel symbols (each 0..3).
//   message     "<call> <grid4> <power_dBm>", e.g. "KB2UKA FN12 30"
//   symbols     caller-allocated output, length >= max_symbols
//   max_symbols capacity (must be >= 162)
// Returns 162 on success, or <0 on error (bad args / unencodable message).
ZEUS_WSPR_API int32_t zeus_wspr_encode(const char* message,
                                       uint8_t* symbols, int32_t max_symbols);

// WSPR tone spacing / symbol rate (Hz / baud) — 12000/8192, the canonical
// values independent of the playback sample rate.
#define ZEUS_WSPR_TONE_SPACING_HZ (12000.0 / 8192.0)
#define ZEUS_WSPR_SYMBOL_PERIOD_S (8192.0 / 12000.0)

// Synthesize continuous-phase 4-FSK audio for a WSPR symbol sequence (the TX
// beacon waveform; also drives the decode round-trip test). WSPR is plain
// continuous-phase FSK — no Gaussian pulse shaping — with short cosine ramps at
// the ends to limit key clicks.
//   symbols     n_sym tone indices (0..3) from zeus_wspr_encode
//   n_sym       number of symbols (162 for a standard WSPR transmission)
//   f0_hz       audio frequency of tone 0, e.g. 1500
//   sample_rate Hz, e.g. 12000
//   audio       caller-allocated output (length >= max_samples)
//   max_samples capacity
// Returns the number of samples written, or <0 on error.
ZEUS_WSPR_API int32_t zeus_wspr_synth(const uint8_t* symbols, int32_t n_sym,
                                      float f0_hz, int32_t sample_rate,
                                      float* audio, int32_t max_samples);

// One decoded WSPR spot.
typedef struct
{
    float snr_db;     // signal-to-noise ratio, dB (2500 Hz reference)
    float dt_sec;     // time offset, seconds
    float freq_mhz;   // absolute decoded frequency, MHz
    int32_t drift_hz; // frequency drift, Hz
    char message[28]; // "<call> <grid4> <power>" (null-terminated)
} zeus_wspr_spot_t;

// Decode one WSPR slot of mono audio.
//   samples       mono float PCM in [-1,1] at sample_rate (≈114 s worth)
//   n             number of samples
//   sample_rate   Hz (12000 canonical)
//   dial_freq_mhz transceiver dial frequency in MHz (labels the decoded freq)
//   out           caller-allocated array of spots
//   max_results   capacity of out
// Returns the number of spots decoded (>=0), or <0 on error.
//
// Thread-safety: serialised internally (the vendored decoder has process-global
// state). WSPR decodes once per 120 s, so serialisation across RX slices is fine.
ZEUS_WSPR_API int32_t zeus_wspr_decode(const float* samples, int32_t n,
                                       int32_t sample_rate, double dial_freq_mhz,
                                       zeus_wspr_spot_t* out, int32_t max_results);

// Library version string (diagnostics / about panel).
ZEUS_WSPR_API const char* zeus_wspr_version(void);

#ifdef __cplusplus
}
#endif

#endif // ZEUS_WSPR_H

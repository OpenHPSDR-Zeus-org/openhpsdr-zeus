/* SPDX-License-Identifier: BSD-2-Clause
 *
 * zeus_rade — a thin single-entry shim over RADE V1 (radae_nopy) + the FARGAN
 * vocoder (Opus), packaged for Zeus. It hides RADE's two-stage decode behind one
 * streaming "complex IQ in -> 16 kHz PCM out" surface so the .NET side P/Invokes
 * a single library instead of binding both rade_api.h AND the Opus FARGAN API.
 *
 * Pipeline (per the verified radae_nopy reference, 2026-06-23):
 *   rade_rx(IQ@8kHz) -> vocoder feature frames (36 floats/frame)
 *   FARGAN: prime with the first 5 frames, then synthesize 160 PCM@16kHz / frame.
 * Both RADE and FARGAN weights are compiled in — no model files at runtime.
 *
 * All sample rates / buffer sizes come from the underlying rade_api.h:
 *   modem IQ  = RADE_MODEM_SAMPLE_RATE  (8000)
 *   speech    = RADE_SPEECH_SAMPLE_RATE (16000)
 */
#ifndef ZEUS_RADE_H
#define ZEUS_RADE_H

#include "rade_api.h"   /* RADE_COMP, RADE_MODEM_SAMPLE_RATE, RADE_SPEECH_SAMPLE_RATE */

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _WIN32
#  define ZEUS_RADE_EXPORT __declspec(dllexport)
#else
#  define ZEUS_RADE_EXPORT __attribute__((visibility("default")))
#endif

typedef struct zeus_rade zeus_rade;

/* Library lifecycle (wrap rade_initialize/finalize; call once per process). */
ZEUS_RADE_EXPORT void        zeus_rade_global_init(void);
ZEUS_RADE_EXPORT void        zeus_rade_global_shutdown(void);

/* Open/close a decoder context. Returns NULL on failure. */
ZEUS_RADE_EXPORT zeus_rade  *zeus_rade_open(void);
ZEUS_RADE_EXPORT void        zeus_rade_close(zeus_rade *z);

/* Buffer-sizing helpers (call after open). */
ZEUS_RADE_EXPORT int  zeus_rade_nin(zeus_rade *z);       /* IQ samples for the NEXT rx call (varies) */
ZEUS_RADE_EXPORT int  zeus_rade_nin_max(zeus_rade *z);   /* max ever — size rx_in[] with this */
ZEUS_RADE_EXPORT int  zeus_rade_max_pcm_per_rx(zeus_rade *z); /* upper bound on PCM produced per rx call */

/* Decode one block of IQ to 16 kHz PCM.
 *   rx_in   : zeus_rade_nin(z) complex (interleaved real,imag) samples @ 8 kHz
 *   pcm_out : caller buffer, >= zeus_rade_max_pcm_per_rx(z) int16 samples @ 16 kHz
 * Returns the number of int16 PCM samples written (0 while unsynced / priming). */
ZEUS_RADE_EXPORT int  zeus_rade_rx(zeus_rade *z, const RADE_COMP *rx_in, short *pcm_out);

/* Telemetry (valid when synced). */
ZEUS_RADE_EXPORT int   zeus_rade_sync(zeus_rade *z);
ZEUS_RADE_EXPORT float zeus_rade_freq_offset(zeus_rade *z);
ZEUS_RADE_EXPORT int   zeus_rade_snr_db(zeus_rade *z);

/* Last decoded End-of-Over callsign (RADE carries up to 8 chars in the EOO
 * frame). Copies into callsign_out (>= 9 bytes); returns chars written, 0 if
 * none since the last over. Maps to the existing FreeDV RX-text UI. */
ZEUS_RADE_EXPORT int   zeus_rade_get_eoo_callsign(zeus_rade *z, char *callsign_out);

#ifdef __cplusplus
}
#endif
#endif /* ZEUS_RADE_H */

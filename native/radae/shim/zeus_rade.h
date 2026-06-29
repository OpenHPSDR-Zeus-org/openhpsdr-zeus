/* SPDX-License-Identifier: BSD-2-Clause
 *
 * zeus_rade — a thin single-entry shim over RADE V1 (radae_c) + the Opus DNN
 * FARGAN vocoder + LPCNet feature analyzer, packaged for Zeus. It hides RADE's
 * two-stage RX/TX behind one streaming surface so the .NET side P/Invokes a
 * single library instead of binding rade_api.h AND the Opus DNN APIs.
 *
 * RX: rade_rx(IQ@8kHz) -> feature frames (36 floats) -> FARGAN -> 16 kHz PCM
 *     (prime with the first 5 frames, then 160 PCM/frame).
 * TX: 16 kHz PCM -> LPCNet analyzer -> feature frames -> rade_tx -> modem IQ@8kHz
 *     (transmit the real part). The EOO callsign uses the FreeDV reliable-text
 *     LDPC (rade_text), interoperable with FreeDV-GUI.
 * RADE + FARGAN weights and the LPCNet analyzer are compiled in — no model files.
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

/* ---- Transmit (mirror of the RX surface) -------------------------------- *
 * RADE TX turns 16 kHz speech into the modem waveform. Internally:
 *   speech 16 kHz --(opus_dnn LPCNet feature analyzer)--> 36-float frames
 *               --(rade_tx)--> RADE_COMP modem IQ @ 8 kHz.
 * The transmitted SSB audio is the REAL part of the modem IQ (the OFDM sits at
 * an audio passband, exactly the inverse of the RX feed which uses imag=0). The
 * shim consumes a fixed frame-group of speech per call and emits a fixed block
 * of modem samples — frame-synchronous, no priming. */

/* TX sizing (call after open). */
ZEUS_RADE_EXPORT int  zeus_rade_n_speech_samples(zeus_rade *z); /* int16 @16k consumed per tx call */
ZEUS_RADE_EXPORT int  zeus_rade_n_tx_out(zeus_rade *z);         /* RADE_COMP produced per tx call */
ZEUS_RADE_EXPORT int  zeus_rade_n_tx_eoo_out(zeus_rade *z);     /* RADE_COMP in the End-of-Over frame */

/* Encode one frame-group of speech to modem IQ.
 *   pcm_in  : zeus_rade_n_speech_samples(z) int16 samples @ 16 kHz
 *   tx_out  : caller buffer, >= zeus_rade_n_tx_out(z) RADE_COMP @ 8 kHz
 * Returns the number of RADE_COMP samples written. */
ZEUS_RADE_EXPORT int  zeus_rade_tx(zeus_rade *z, const short *pcm_in, RADE_COMP *tx_out);

/* Flush the End-of-Over frame (carries the callsign set via set_tx_callsign).
 * Call once on un-key, after the last zeus_rade_tx.
 *   tx_eoo_out : caller buffer, >= zeus_rade_n_tx_eoo_out(z) RADE_COMP @ 8 kHz
 * Returns the number of RADE_COMP samples written. */
ZEUS_RADE_EXPORT int  zeus_rade_tx_eoo(zeus_rade *z, RADE_COMP *tx_eoo_out);

/* Set the callsign embedded in the EOO frame (<= 8 chars). Encoded with the
 * FreeDV reliable-text LDPC (rade_text) so it interoperates with FreeDV-GUI RADE
 * stations — NOT radae_c's internal 7-bit packing. Empty/NULL clears it. */
ZEUS_RADE_EXPORT void zeus_rade_set_tx_callsign(zeus_rade *z, const char *callsign);

/* Telemetry (valid when synced). */
ZEUS_RADE_EXPORT int   zeus_rade_sync(zeus_rade *z);
ZEUS_RADE_EXPORT float zeus_rade_freq_offset(zeus_rade *z);
ZEUS_RADE_EXPORT int   zeus_rade_snr_db(zeus_rade *z);

/* Last decoded End-of-Over callsign (RADE carries up to 8 chars in the EOO
 * frame). Decoded with the FreeDV reliable-text LDPC (rade_text) — CRC-checked,
 * so it interoperates with FreeDV-GUI RADE stations. Copies into callsign_out
 * (>= 9 bytes); returns chars written, 0 if none since the last over. Maps to
 * the existing FreeDV RX-text UI. */
ZEUS_RADE_EXPORT int   zeus_rade_get_eoo_callsign(zeus_rade *z, char *callsign_out);

#ifdef __cplusplus
}
#endif
#endif /* ZEUS_RADE_H */

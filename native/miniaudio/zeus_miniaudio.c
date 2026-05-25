/*
 * zeus_miniaudio.c — implementation of the Zeus C ABI on top of miniaudio.
 *
 * This is the ONLY translation unit that compiles the miniaudio
 * implementation (via MINIAUDIO_IMPLEMENTATION). Everything in
 * zeus_miniaudio.h is a thin wrapper around ma_device_init /
 * ma_device_start / ma_device_stop / ma_device_uninit, holding an opaque
 * ma_device per Zeus handle.
 *
 * Format is fixed: ma_format_f32. Channels and sample rate are negotiated —
 * if the operator asks for prefer_*=0 we let miniaudio pick the device
 * native rate / channel count, and the C# side reads the actual values back
 * via zeus_ma_*_sample_rate / _channels. Resampling between the device's
 * actual rate and Zeus's 48 kHz pipeline is handled by miniaudio's internal
 * resampler — configured below with ma_device_config.sampleRate=0 and
 * resampling.algorithm=ma_resample_algorithm_linear (low-CPU, low-latency,
 * adequate for voice).
 *
 * Copyright (C) 2026 Brian Keating (EI6LF) and contributors.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */

/* Trim out parts of miniaudio Zeus doesn't use, before the implementation
 * is generated. Keeps the binary lean — Zeus only does raw device I/O. */
#define MA_NO_DECODING
#define MA_NO_ENCODING
#define MA_NO_GENERATION
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_ENGINE
#define MA_NO_NODE_GRAPH

#define MINIAUDIO_IMPLEMENTATION
#include "miniaudio.h"

#include "zeus_miniaudio.h"

#include <stdlib.h>
#include <string.h>

/* ------------------------------------------------------------------------ */
/* Handle types                                                             */
/* ------------------------------------------------------------------------ */

typedef struct {
    ma_device                device;
    zeus_ma_playback_cb      data_cb;
    zeus_ma_notify_cb        notify_cb;
    void*                    user;
    uint32_t                 negotiated_rate;
    uint32_t                 negotiated_channels;
    /* The share mode actually achieved by ma_device_init: 1 = exclusive,
     * 0 = shared. We try exclusive first (closest match to Thetis's direct
     * native output path — bypasses the Windows audio engine / mixer and
     * the RDP shared-audio layer that adds the ~1.7 s TX→RX resume delay)
     * and fall back to shared when the device refuses exclusive. */
    int32_t                  share_mode_exclusive;
} zeus_ma_output;

typedef struct {
    ma_device                device;
    zeus_ma_capture_cb       data_cb;
    zeus_ma_notify_cb        notify_cb;
    void*                    user;
    uint32_t                 negotiated_rate;
    uint32_t                 negotiated_channels;
    int32_t                  share_mode_exclusive;
} zeus_ma_input;

/* ------------------------------------------------------------------------ */
/* miniaudio callbacks (audio worker thread)                                */
/* ------------------------------------------------------------------------ */

static void zeus_ma_output_data_proc(ma_device* dev, void* pOutput, const void* pInput, ma_uint32 frame_count)
{
    (void)pInput;
    zeus_ma_output* h = (zeus_ma_output*)dev->pUserData;
    if (h == NULL || h->data_cb == NULL) {
        /* miniaudio expects us to fill the buffer — write silence rather
         * than leave it stale. */
        if (pOutput != NULL && frame_count > 0) {
            memset(pOutput, 0, (size_t)frame_count * dev->playback.channels * sizeof(float));
        }
        return;
    }
    h->data_cb(h->user, (float*)pOutput, (uint32_t)frame_count, (uint32_t)dev->playback.channels);
}

static void zeus_ma_input_data_proc(ma_device* dev, void* pOutput, const void* pInput, ma_uint32 frame_count)
{
    (void)pOutput;
    zeus_ma_input* h = (zeus_ma_input*)dev->pUserData;
    if (h == NULL || h->data_cb == NULL || pInput == NULL) {
        return;
    }
    h->data_cb(h->user, (const float*)pInput, (uint32_t)frame_count, (uint32_t)dev->capture.channels);
}

static int32_t zeus_ma_translate_notification(ma_device_notification_type t)
{
    switch (t) {
        case ma_device_notification_type_started:           return 1;
        case ma_device_notification_type_stopped:           return 2;
        case ma_device_notification_type_rerouted:          return 3;
        case ma_device_notification_type_interruption_began: return 4;
        case ma_device_notification_type_interruption_ended: return 5;
        case ma_device_notification_type_unlocked:          return 6;
        default:                                            return 0;
    }
}

static void zeus_ma_output_notify_proc(const ma_device_notification* n)
{
    zeus_ma_output* h = (zeus_ma_output*)n->pDevice->pUserData;
    if (h && h->notify_cb) {
        h->notify_cb(h->user, zeus_ma_translate_notification(n->type));
    }
}

static void zeus_ma_input_notify_proc(const ma_device_notification* n)
{
    zeus_ma_input* h = (zeus_ma_input*)n->pDevice->pUserData;
    if (h && h->notify_cb) {
        h->notify_cb(h->user, zeus_ma_translate_notification(n->type));
    }
}

/* ------------------------------------------------------------------------ */
/* Playback                                                                 */
/* ------------------------------------------------------------------------ */

ZEUS_MA_EXPORT void* zeus_ma_output_create(
    uint32_t prefer_sample_rate,
    uint32_t prefer_channels,
    uint32_t period_frames,
    uint32_t periods,
    zeus_ma_playback_cb data_cb,
    zeus_ma_notify_cb notify_cb,
    void* user)
{
    if (data_cb == NULL) return NULL;

    zeus_ma_output* h = (zeus_ma_output*)calloc(1, sizeof(*h));
    if (h == NULL) return NULL;

    h->data_cb   = data_cb;
    h->notify_cb = notify_cb;
    h->user      = user;

    ma_device_config cfg = ma_device_config_init(ma_device_type_playback);
    cfg.playback.format    = ma_format_f32;
    cfg.playback.channels  = prefer_channels;        /* 0 = device native */
    cfg.sampleRate         = prefer_sample_rate;     /* 0 = device native */
    cfg.periodSizeInFrames = period_frames;          /* 0 = miniaudio default */
    cfg.periods            = periods;                /* 0 = miniaudio default */
    cfg.dataCallback       = zeus_ma_output_data_proc;
    cfg.notificationCallback = zeus_ma_output_notify_proc;
    cfg.pUserData          = h;
    /* Linear resampler: lowest CPU + lowest added latency. Adequate for
     * voice / SSB / FM audio at 48 kHz. */
    cfg.resampling.algorithm = ma_resample_algorithm_linear;
    /* Performance + scheduler hints. Documented intent: ask the OS for the
     * smallest period the driver supports (functionally the default on
     * miniaudio — ma_performance_profile_low_latency == 0 and the config
     * struct is zero-init), and tell Windows WASAPI this is a real-time
     * audio app so AvSetMmThreadCharacteristics raises the audio worker
     * thread to the "Pro Audio" MMCSS class. The Pro Audio class biases
     * the scheduler away from preempting our callback under load, which
     * is the OS-side counterpart to the dropout-resilience the C# ring
     * already provides. wasapi.usage is a no-op on macOS / Linux backends. */
    cfg.performanceProfile = ma_performance_profile_low_latency;
    cfg.wasapi.usage       = ma_wasapi_usage_pro_audio;
    /* WASAPI low-latency fix: disable AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM.
     * On an RDP "Remote Audio" endpoint (and many real cards) the device's
     * native shared-mode rate is not 48 kHz. With AUTOCONVERTPCM on, WASAPI
     * silently rejects the IAudioClient3 low-latency path on a sample-rate
     * mismatch and falls back to the ~1–2 s shared-mode default buffer —
     * which is exactly the ~1.7 s TX→RX audio-resume delay reported in #468.
     * Turning AUTOCONVERTPCM off lets miniaudio's own linear resampler do the
     * 48k↔device-rate conversion (already configured above) while IAudioClient3
     * keeps the buffer at ~20 ms. WASAPI-only field; a no-op on the
     * CoreAudio / ALSA / PulseAudio / DirectSound / WinMM backends. */
    cfg.wasapi.noAutoConvertSRC = MA_TRUE;

    /* Exclusive-mode first, shared as graceful fallback.
     *
     * Thetis keeps a single continuous RX output stream warm and mutes at
     * the mixer during TX; its native ChannelMaster output opens the device
     * on a low-latency / direct path. WASAPI SHARED mode routes our audio
     * through the Windows audio engine + mixer (and, under RDP, the
     * "Remote Audio" shared-audio layer) which buffers deeply and is what
     * drains + re-primes for ~1.7 s on un-key (#468). EXCLUSIVE mode bypasses
     * the engine/mixer entirely — the closest match to Thetis's direct path.
     *
     * Many endpoints refuse exclusive (RDP "Remote Audio" virtual devices in
     * particular, plus devices already opened exclusively by another app, and
     * some shared-only virtual cables). When ma_device_init fails in exclusive
     * we MUST retry shared so the app never fails to open audio — that retry
     * is also the cross-platform safety net: on macOS / Linux, exclusive maps
     * to CoreAudio hog mode / ALSA hw access which frequently fail, and the
     * fallback lands us on today's shared behaviour. share_mode_exclusive
     * records which path actually opened so the C# log can surface it. */
    cfg.playback.shareMode = ma_share_mode_exclusive;
    if (ma_device_init(NULL, &cfg, &h->device) == MA_SUCCESS) {
        h->share_mode_exclusive = 1;
    } else {
        cfg.playback.shareMode = ma_share_mode_shared;
        if (ma_device_init(NULL, &cfg, &h->device) != MA_SUCCESS) {
            free(h);
            return NULL;
        }
        h->share_mode_exclusive = 0;
    }

    h->negotiated_rate     = h->device.sampleRate;
    h->negotiated_channels = h->device.playback.channels;
    return h;
}

ZEUS_MA_EXPORT int32_t zeus_ma_output_start(void* handle)
{
    if (handle == NULL) return -1;
    zeus_ma_output* h = (zeus_ma_output*)handle;
    return (ma_device_start(&h->device) == MA_SUCCESS) ? 0 : -1;
}

ZEUS_MA_EXPORT int32_t zeus_ma_output_stop(void* handle)
{
    if (handle == NULL) return -1;
    zeus_ma_output* h = (zeus_ma_output*)handle;
    return (ma_device_stop(&h->device) == MA_SUCCESS) ? 0 : -1;
}

ZEUS_MA_EXPORT uint32_t zeus_ma_output_sample_rate(void* handle)
{
    if (handle == NULL) return 0;
    return ((zeus_ma_output*)handle)->negotiated_rate;
}

ZEUS_MA_EXPORT uint32_t zeus_ma_output_channels(void* handle)
{
    if (handle == NULL) return 0;
    return ((zeus_ma_output*)handle)->negotiated_channels;
}

ZEUS_MA_EXPORT const char* zeus_ma_output_backend_name(void* handle)
{
    if (handle == NULL) return "none";
    zeus_ma_output* h = (zeus_ma_output*)handle;
    if (h->device.pContext == NULL) return "none";
    /* Returns a static, NUL-terminated string ("WASAPI", "DirectSound",
     * "WinMM", "Core Audio", "ALSA", "PulseAudio", "Null", ...). Confirms
     * which backend miniaudio actually selected — the WASAPI low-latency fix
     * only applies when this reads "WASAPI". */
    return ma_get_backend_name(h->device.pContext->backend);
}

ZEUS_MA_EXPORT uint32_t zeus_ma_output_buffer_frames(void* handle)
{
    if (handle == NULL) return 0;
    /* The negotiated internal period size in frames. Multiplied by the rate
     * this is the per-period latency; a ~20 ms low-latency period at 48 kHz
     * is ~960 frames, a deep ~1.7 s shared-mode buffer is tens of thousands. */
    return ((zeus_ma_output*)handle)->device.playback.internalPeriodSizeInFrames;
}

ZEUS_MA_EXPORT uint32_t zeus_ma_output_periods(void* handle)
{
    if (handle == NULL) return 0;
    /* Number of internal periods. buffer_frames * periods / rate = total
     * device buffer latency in seconds. */
    return ((zeus_ma_output*)handle)->device.playback.internalPeriods;
}

ZEUS_MA_EXPORT int32_t zeus_ma_output_share_mode_exclusive(void* handle)
{
    if (handle == NULL) return 0;
    /* 1 = the device opened in WASAPI / CoreAudio / ALSA exclusive mode
     * (direct path, no shared mixer); 0 = it fell back to shared. */
    return ((zeus_ma_output*)handle)->share_mode_exclusive;
}

ZEUS_MA_EXPORT void zeus_ma_output_destroy(void* handle)
{
    if (handle == NULL) return;
    zeus_ma_output* h = (zeus_ma_output*)handle;
    ma_device_uninit(&h->device);
    free(h);
}

/* ------------------------------------------------------------------------ */
/* Capture                                                                  */
/* ------------------------------------------------------------------------ */

ZEUS_MA_EXPORT void* zeus_ma_input_create(
    uint32_t prefer_sample_rate,
    uint32_t prefer_channels,
    uint32_t period_frames,
    uint32_t periods,
    zeus_ma_capture_cb data_cb,
    zeus_ma_notify_cb notify_cb,
    void* user)
{
    if (data_cb == NULL) return NULL;

    zeus_ma_input* h = (zeus_ma_input*)calloc(1, sizeof(*h));
    if (h == NULL) return NULL;

    h->data_cb   = data_cb;
    h->notify_cb = notify_cb;
    h->user      = user;

    ma_device_config cfg = ma_device_config_init(ma_device_type_capture);
    cfg.capture.format     = ma_format_f32;
    cfg.capture.channels   = prefer_channels;
    cfg.sampleRate         = prefer_sample_rate;
    cfg.periodSizeInFrames = period_frames;
    cfg.periods            = periods;
    cfg.dataCallback       = zeus_ma_input_data_proc;
    cfg.notificationCallback = zeus_ma_input_notify_proc;
    cfg.pUserData          = h;
    cfg.resampling.algorithm = ma_resample_algorithm_linear;
    /* Same performance + scheduler hints as the playback side — see the
     * comment in zeus_ma_output_create. The mic-capture worker runs
     * continuously while desktop mode is up, so MMCSS Pro Audio class
     * helps it survive OS-level preemption the same way it helps the
     * playback thread. */
    cfg.performanceProfile = ma_performance_profile_low_latency;
    cfg.wasapi.usage       = ma_wasapi_usage_pro_audio;
    /* WASAPI low-latency fix — same rationale as the playback side. The
     * capture device on a non-48k endpoint would otherwise also fall back to
     * the deep shared-mode buffer. WASAPI-only; no-op elsewhere. */
    cfg.wasapi.noAutoConvertSRC = MA_TRUE;

    /* Exclusive-first with shared fallback — same rationale and safety net as
     * the playback side (see zeus_ma_output_create). The mic device on an RDP
     * "Remote Audio" endpoint will typically refuse exclusive and fall back to
     * shared; on a real local card exclusive keeps it off the shared mixer. */
    cfg.capture.shareMode = ma_share_mode_exclusive;
    if (ma_device_init(NULL, &cfg, &h->device) == MA_SUCCESS) {
        h->share_mode_exclusive = 1;
    } else {
        cfg.capture.shareMode = ma_share_mode_shared;
        if (ma_device_init(NULL, &cfg, &h->device) != MA_SUCCESS) {
            free(h);
            return NULL;
        }
        h->share_mode_exclusive = 0;
    }

    h->negotiated_rate     = h->device.sampleRate;
    h->negotiated_channels = h->device.capture.channels;
    return h;
}

ZEUS_MA_EXPORT int32_t zeus_ma_input_start(void* handle)
{
    if (handle == NULL) return -1;
    zeus_ma_input* h = (zeus_ma_input*)handle;
    return (ma_device_start(&h->device) == MA_SUCCESS) ? 0 : -1;
}

ZEUS_MA_EXPORT int32_t zeus_ma_input_stop(void* handle)
{
    if (handle == NULL) return -1;
    zeus_ma_input* h = (zeus_ma_input*)handle;
    return (ma_device_stop(&h->device) == MA_SUCCESS) ? 0 : -1;
}

ZEUS_MA_EXPORT uint32_t zeus_ma_input_sample_rate(void* handle)
{
    if (handle == NULL) return 0;
    return ((zeus_ma_input*)handle)->negotiated_rate;
}

ZEUS_MA_EXPORT uint32_t zeus_ma_input_channels(void* handle)
{
    if (handle == NULL) return 0;
    return ((zeus_ma_input*)handle)->negotiated_channels;
}

ZEUS_MA_EXPORT const char* zeus_ma_input_backend_name(void* handle)
{
    if (handle == NULL) return "none";
    zeus_ma_input* h = (zeus_ma_input*)handle;
    if (h->device.pContext == NULL) return "none";
    return ma_get_backend_name(h->device.pContext->backend);
}

ZEUS_MA_EXPORT uint32_t zeus_ma_input_buffer_frames(void* handle)
{
    if (handle == NULL) return 0;
    return ((zeus_ma_input*)handle)->device.capture.internalPeriodSizeInFrames;
}

ZEUS_MA_EXPORT uint32_t zeus_ma_input_periods(void* handle)
{
    if (handle == NULL) return 0;
    return ((zeus_ma_input*)handle)->device.capture.internalPeriods;
}

ZEUS_MA_EXPORT int32_t zeus_ma_input_share_mode_exclusive(void* handle)
{
    if (handle == NULL) return 0;
    return ((zeus_ma_input*)handle)->share_mode_exclusive;
}

ZEUS_MA_EXPORT void zeus_ma_input_destroy(void* handle)
{
    if (handle == NULL) return;
    zeus_ma_input* h = (zeus_ma_input*)handle;
    ma_device_uninit(&h->device);
    free(h);
}

/* ------------------------------------------------------------------------ */
/* Version probe                                                            */
/* ------------------------------------------------------------------------ */

ZEUS_MA_EXPORT const char* zeus_ma_version(void)
{
    /* String literal, NUL-terminated, safe to PtrToStringAnsi on the C# side. */
    return "zeus-miniaudio " MA_VERSION_STRING;
}

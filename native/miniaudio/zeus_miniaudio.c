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
} zeus_ma_output;

typedef struct {
    ma_device                device;
    zeus_ma_capture_cb       data_cb;
    zeus_ma_notify_cb        notify_cb;
    void*                    user;
    uint32_t                 negotiated_rate;
    uint32_t                 negotiated_channels;
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
    /* CRITICAL for low output latency (issue #468). WASAPI shared-mode
     * low-latency (IAudioClient3_InitializeSharedAudioStream) is SILENTLY
     * DISABLED whenever the app-requested sample rate differs from the
     * device's native rate, because miniaudio then sets
     * AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM and IAudioClient3 rejects it
     * (miniaudio.h §15.1). When that happens miniaudio falls back to the
     * DEFAULT shared-mode buffer, which on some endpoints — notably RDP /
     * remote-desktop virtual audio, but also any device whose native rate
     * isn't 48 kHz (e.g. a 44.1 kHz endpoint) — is ~1–2 SECONDS deep. That
     * deep buffer is exactly the PublishAudio→audible gap the t4 resume probe
     * measured at +1677 ms while the backend delivered audio in ~22 ms.
     *
     * Setting noAutoConvertSRC keeps IAudioClient3 happy (no AUTOCONVERTPCM
     * flag), so low-latency shared mode engages and the buffer collapses to
     * the requested ~20 ms (periodFrames*periods below). The device's own
     * rate conversion is replaced by miniaudio's internal resampler, already
     * configured to linear above — adequate for 48 kHz voice/SSB and a
     * negligible CPU cost. WASAPI-only field; a harmless no-op on the
     * CoreAudio / ALSA / PulseAudio backends, so macOS and Linux are
     * unaffected. The device stays ONE continuously-running stream — no
     * stop/start, no ramp. */
    cfg.wasapi.noAutoConvertSRC = MA_TRUE;

    if (ma_device_init(NULL, &cfg, &h->device) != MA_SUCCESS) {
        free(h);
        return NULL;
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

    if (ma_device_init(NULL, &cfg, &h->device) != MA_SUCCESS) {
        free(h);
        return NULL;
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

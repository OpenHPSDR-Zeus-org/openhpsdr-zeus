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
 * Copyright (C) 2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
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

typedef struct {
    char                     id_hex[(sizeof(ma_device_id) * 2) + 1];
    char                     name[MA_MAX_DEVICE_NAME_LENGTH + 1];
    int32_t                  is_default;
} zeus_ma_device_snapshot_entry;

typedef struct {
    uint32_t                         playback_count;
    uint32_t                         capture_count;
    zeus_ma_device_snapshot_entry*   playback;
    zeus_ma_device_snapshot_entry*   capture;
} zeus_ma_device_snapshot;

/* ------------------------------------------------------------------------ */
/* Device id helpers                                                        */
/* ------------------------------------------------------------------------ */

static char zeus_ma_hex_digit(unsigned int v)
{
    return (char)(v < 10 ? ('0' + v) : ('a' + (v - 10)));
}

static int zeus_ma_hex_value(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
    if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
    return -1;
}

static void zeus_ma_encode_device_id(const ma_device_id* id, char* out_hex)
{
    const unsigned char* bytes = (const unsigned char*)id;
    size_t i;
    for (i = 0; i < sizeof(ma_device_id); i += 1) {
        out_hex[i * 2] = zeus_ma_hex_digit((bytes[i] >> 4) & 0x0F);
        out_hex[(i * 2) + 1] = zeus_ma_hex_digit(bytes[i] & 0x0F);
    }
    out_hex[sizeof(ma_device_id) * 2] = '\0';
}

static ma_bool32 zeus_ma_decode_device_id(const char* hex, ma_device_id* out_id)
{
    size_t expected = sizeof(ma_device_id) * 2;
    size_t i;
    unsigned char* bytes;
    if (hex == NULL || out_id == NULL) return MA_FALSE;
    if (strlen(hex) != expected) return MA_FALSE;

    memset(out_id, 0, sizeof(*out_id));
    bytes = (unsigned char*)out_id;
    for (i = 0; i < sizeof(ma_device_id); i += 1) {
        int hi = zeus_ma_hex_value(hex[i * 2]);
        int lo = zeus_ma_hex_value(hex[(i * 2) + 1]);
        if (hi < 0 || lo < 0) return MA_FALSE;
        bytes[i] = (unsigned char)((hi << 4) | lo);
    }
    return MA_TRUE;
}

static void zeus_ma_copy_device_entries(
    zeus_ma_device_snapshot_entry* dst,
    const ma_device_info* src,
    uint32_t count)
{
    uint32_t i;
    for (i = 0; i < count; i += 1) {
        zeus_ma_encode_device_id(&src[i].id, dst[i].id_hex);
        strncpy(dst[i].name, src[i].name, MA_MAX_DEVICE_NAME_LENGTH);
        dst[i].name[MA_MAX_DEVICE_NAME_LENGTH] = '\0';
        dst[i].is_default = src[i].isDefault ? 1 : 0;
    }
}

static void zeus_ma_device_snapshot_free(zeus_ma_device_snapshot* s)
{
    if (s == NULL) return;
    free(s->playback);
    free(s->capture);
    free(s);
}

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

static void* zeus_ma_output_create_internal(
    uint32_t prefer_sample_rate,
    uint32_t prefer_channels,
    uint32_t period_frames,
    uint32_t periods,
    const char* device_id_hex,
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

    ma_device_id requested_id;
    ma_device_id* requested_id_ptr = NULL;
    if (device_id_hex != NULL && device_id_hex[0] != '\0') {
        if (!zeus_ma_decode_device_id(device_id_hex, &requested_id)) {
            free(h);
            return NULL;
        }
        requested_id_ptr = &requested_id;
    }

    ma_device_config cfg = ma_device_config_init(ma_device_type_playback);
    cfg.playback.pDeviceID = requested_id_ptr;
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

    if (ma_device_init(NULL, &cfg, &h->device) != MA_SUCCESS) {
        free(h);
        return NULL;
    }

    h->negotiated_rate     = h->device.sampleRate;
    h->negotiated_channels = h->device.playback.channels;
    return h;
}

ZEUS_MA_EXPORT void* zeus_ma_output_create(
    uint32_t prefer_sample_rate,
    uint32_t prefer_channels,
    uint32_t period_frames,
    uint32_t periods,
    zeus_ma_playback_cb data_cb,
    zeus_ma_notify_cb notify_cb,
    void* user)
{
    return zeus_ma_output_create_internal(
        prefer_sample_rate,
        prefer_channels,
        period_frames,
        periods,
        NULL,
        data_cb,
        notify_cb,
        user);
}

ZEUS_MA_EXPORT void* zeus_ma_output_create_for_device(
    uint32_t prefer_sample_rate,
    uint32_t prefer_channels,
    uint32_t period_frames,
    uint32_t periods,
    const char* device_id_hex,
    zeus_ma_playback_cb data_cb,
    zeus_ma_notify_cb notify_cb,
    void* user)
{
    return zeus_ma_output_create_internal(
        prefer_sample_rate,
        prefer_channels,
        period_frames,
        periods,
        device_id_hex,
        data_cb,
        notify_cb,
        user);
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

static void* zeus_ma_input_create_internal(
    uint32_t prefer_sample_rate,
    uint32_t prefer_channels,
    uint32_t period_frames,
    uint32_t periods,
    const char* device_id_hex,
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

    ma_device_id requested_id;
    ma_device_id* requested_id_ptr = NULL;
    if (device_id_hex != NULL && device_id_hex[0] != '\0') {
        if (!zeus_ma_decode_device_id(device_id_hex, &requested_id)) {
            free(h);
            return NULL;
        }
        requested_id_ptr = &requested_id;
    }

    ma_device_config cfg = ma_device_config_init(ma_device_type_capture);
    cfg.capture.pDeviceID = requested_id_ptr;
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

ZEUS_MA_EXPORT void* zeus_ma_input_create(
    uint32_t prefer_sample_rate,
    uint32_t prefer_channels,
    uint32_t period_frames,
    uint32_t periods,
    zeus_ma_capture_cb data_cb,
    zeus_ma_notify_cb notify_cb,
    void* user)
{
    return zeus_ma_input_create_internal(
        prefer_sample_rate,
        prefer_channels,
        period_frames,
        periods,
        NULL,
        data_cb,
        notify_cb,
        user);
}

ZEUS_MA_EXPORT void* zeus_ma_input_create_for_device(
    uint32_t prefer_sample_rate,
    uint32_t prefer_channels,
    uint32_t period_frames,
    uint32_t periods,
    const char* device_id_hex,
    zeus_ma_capture_cb data_cb,
    zeus_ma_notify_cb notify_cb,
    void* user)
{
    return zeus_ma_input_create_internal(
        prefer_sample_rate,
        prefer_channels,
        period_frames,
        periods,
        device_id_hex,
        data_cb,
        notify_cb,
        user);
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
/* Device enumeration                                                       */
/* ------------------------------------------------------------------------ */

ZEUS_MA_EXPORT void* zeus_ma_devices_create(void)
{
    ma_context ctx;
    ma_device_info* playback_infos = NULL;
    ma_device_info* capture_infos = NULL;
    ma_uint32 playback_count = 0;
    ma_uint32 capture_count = 0;
    zeus_ma_device_snapshot* s = NULL;

    if (ma_context_init(NULL, 0, NULL, &ctx) != MA_SUCCESS) {
        return NULL;
    }

    if (ma_context_get_devices(&ctx, &playback_infos, &playback_count, &capture_infos, &capture_count) != MA_SUCCESS) {
        ma_context_uninit(&ctx);
        return NULL;
    }

    s = (zeus_ma_device_snapshot*)calloc(1, sizeof(*s));
    if (s == NULL) {
        ma_context_uninit(&ctx);
        return NULL;
    }

    s->playback_count = (uint32_t)playback_count;
    s->capture_count = (uint32_t)capture_count;

    if (s->playback_count > 0) {
        s->playback = (zeus_ma_device_snapshot_entry*)calloc(s->playback_count, sizeof(*s->playback));
        if (s->playback == NULL) {
            zeus_ma_device_snapshot_free(s);
            ma_context_uninit(&ctx);
            return NULL;
        }
        zeus_ma_copy_device_entries(s->playback, playback_infos, s->playback_count);
    }

    if (s->capture_count > 0) {
        s->capture = (zeus_ma_device_snapshot_entry*)calloc(s->capture_count, sizeof(*s->capture));
        if (s->capture == NULL) {
            zeus_ma_device_snapshot_free(s);
            ma_context_uninit(&ctx);
            return NULL;
        }
        zeus_ma_copy_device_entries(s->capture, capture_infos, s->capture_count);
    }

    ma_context_uninit(&ctx);
    return s;
}

ZEUS_MA_EXPORT uint32_t zeus_ma_devices_playback_count(void* snapshot)
{
    if (snapshot == NULL) return 0;
    return ((zeus_ma_device_snapshot*)snapshot)->playback_count;
}

ZEUS_MA_EXPORT uint32_t zeus_ma_devices_capture_count(void* snapshot)
{
    if (snapshot == NULL) return 0;
    return ((zeus_ma_device_snapshot*)snapshot)->capture_count;
}

static zeus_ma_device_snapshot_entry* zeus_ma_playback_entry(void* snapshot, uint32_t index)
{
    zeus_ma_device_snapshot* s = (zeus_ma_device_snapshot*)snapshot;
    if (s == NULL || s->playback == NULL || index >= s->playback_count) return NULL;
    return &s->playback[index];
}

static zeus_ma_device_snapshot_entry* zeus_ma_capture_entry(void* snapshot, uint32_t index)
{
    zeus_ma_device_snapshot* s = (zeus_ma_device_snapshot*)snapshot;
    if (s == NULL || s->capture == NULL || index >= s->capture_count) return NULL;
    return &s->capture[index];
}

ZEUS_MA_EXPORT const char* zeus_ma_devices_playback_id(void* snapshot, uint32_t index)
{
    zeus_ma_device_snapshot_entry* e = zeus_ma_playback_entry(snapshot, index);
    return e == NULL ? "" : e->id_hex;
}

ZEUS_MA_EXPORT const char* zeus_ma_devices_capture_id(void* snapshot, uint32_t index)
{
    zeus_ma_device_snapshot_entry* e = zeus_ma_capture_entry(snapshot, index);
    return e == NULL ? "" : e->id_hex;
}

ZEUS_MA_EXPORT const char* zeus_ma_devices_playback_name(void* snapshot, uint32_t index)
{
    zeus_ma_device_snapshot_entry* e = zeus_ma_playback_entry(snapshot, index);
    return e == NULL ? "" : e->name;
}

ZEUS_MA_EXPORT const char* zeus_ma_devices_capture_name(void* snapshot, uint32_t index)
{
    zeus_ma_device_snapshot_entry* e = zeus_ma_capture_entry(snapshot, index);
    return e == NULL ? "" : e->name;
}

ZEUS_MA_EXPORT int32_t zeus_ma_devices_playback_default(void* snapshot, uint32_t index)
{
    zeus_ma_device_snapshot_entry* e = zeus_ma_playback_entry(snapshot, index);
    return e == NULL ? 0 : e->is_default;
}

ZEUS_MA_EXPORT int32_t zeus_ma_devices_capture_default(void* snapshot, uint32_t index)
{
    zeus_ma_device_snapshot_entry* e = zeus_ma_capture_entry(snapshot, index);
    return e == NULL ? 0 : e->is_default;
}

ZEUS_MA_EXPORT void zeus_ma_devices_destroy(void* snapshot)
{
    zeus_ma_device_snapshot_free((zeus_ma_device_snapshot*)snapshot);
}

/* ------------------------------------------------------------------------ */
/* Version probe                                                            */
/* ------------------------------------------------------------------------ */

ZEUS_MA_EXPORT const char* zeus_ma_version(void)
{
    /* String literal, NUL-terminated, safe to PtrToStringAnsi on the C# side. */
    return "zeus-miniaudio " MA_VERSION_STRING;
}

/*
 * zeus_miniaudio.h — minimal C ABI on top of miniaudio for Zeus desktop mode.
 *
 * Public function set is intentionally small (open / start / stop / destroy /
 * query) and uses only stdint + opaque pointers so the C# P/Invoke surface in
 * Zeus.Server.Hosting/MiniAudio* is stable across miniaudio version bumps.
 *
 * Threading model: the data callbacks fire on miniaudio's dedicated audio
 * worker thread. They must not block. Callers are responsible for the SPSC
 * ring discipline on the C# side.
 *
 * Error model: every "create" returns NULL on failure; start/stop/destroy
 * return 0 on success and -1 on failure. miniaudio's verbose ma_result code
 * is intentionally not surfaced — operator-facing logs are produced on the
 * C# side using the success/failure boolean plus the device-info getters.
 *
 * Copyright (C) 2026 Brian Keating (EI6LF) and contributors.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#ifndef ZEUS_MINIAUDIO_H
#define ZEUS_MINIAUDIO_H

#include <stdint.h>

#if defined(_WIN32)
  #define ZEUS_MA_EXPORT __declspec(dllexport)
#else
  #define ZEUS_MA_EXPORT __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Data callback for playback: miniaudio asks Zeus for `frame_count` frames of
 * interleaved f32, `channels` channels. Zeus writes into `out`.
 *
 * Data callback for capture: miniaudio hands Zeus `frame_count` frames of
 * interleaved f32 input from `in`.
 *
 * `user` is the opaque pointer passed to *_create().
 */
typedef void (*zeus_ma_playback_cb)(void* user, float* out, uint32_t frame_count, uint32_t channels);
typedef void (*zeus_ma_capture_cb)(void* user, const float* in, uint32_t frame_count, uint32_t channels);

/* Notification kinds — small superset of miniaudio's ma_device_notification_type.
 * Reroute = output / input device changed (e.g. headphone hotplug, BT switch).
 * 1=started, 2=stopped, 3=rerouted, 4=interruption_began, 5=interruption_ended,
 * 6=unlocked (linux pulseaudio specific, ignored). */
typedef void (*zeus_ma_notify_cb)(void* user, int32_t kind);

/* ---------------- Playback (output device) ----------------------------- */

ZEUS_MA_EXPORT void* zeus_ma_output_create(
    uint32_t prefer_sample_rate,    /* 0 = device native */
    uint32_t prefer_channels,       /* 0 = device native; typically 2 */
    uint32_t period_frames,         /* periodSizeInFrames; 0 = miniaudio default */
    uint32_t periods,               /* number of periods; 0 = miniaudio default */
    zeus_ma_playback_cb data_cb,
    zeus_ma_notify_cb notify_cb,    /* may be NULL */
    void* user);

ZEUS_MA_EXPORT void* zeus_ma_output_create_for_device(
    uint32_t prefer_sample_rate,
    uint32_t prefer_channels,
    uint32_t period_frames,
    uint32_t periods,
    const char* device_id_hex,      /* NULL / empty = default device */
    zeus_ma_playback_cb data_cb,
    zeus_ma_notify_cb notify_cb,
    void* user);

ZEUS_MA_EXPORT int32_t  zeus_ma_output_start(void* handle);
ZEUS_MA_EXPORT int32_t  zeus_ma_output_stop(void* handle);
ZEUS_MA_EXPORT uint32_t zeus_ma_output_sample_rate(void* handle);
ZEUS_MA_EXPORT uint32_t zeus_ma_output_channels(void* handle);
ZEUS_MA_EXPORT void     zeus_ma_output_destroy(void* handle);

/* ---------------- Capture (input device) ------------------------------- */

ZEUS_MA_EXPORT void* zeus_ma_input_create(
    uint32_t prefer_sample_rate,    /* 0 = device native */
    uint32_t prefer_channels,       /* 0 = device native; typically 1 */
    uint32_t period_frames,
    uint32_t periods,
    zeus_ma_capture_cb data_cb,
    zeus_ma_notify_cb notify_cb,
    void* user);

ZEUS_MA_EXPORT void* zeus_ma_input_create_for_device(
    uint32_t prefer_sample_rate,
    uint32_t prefer_channels,
    uint32_t period_frames,
    uint32_t periods,
    const char* device_id_hex,      /* NULL / empty = default device */
    zeus_ma_capture_cb data_cb,
    zeus_ma_notify_cb notify_cb,
    void* user);

ZEUS_MA_EXPORT int32_t  zeus_ma_input_start(void* handle);
ZEUS_MA_EXPORT int32_t  zeus_ma_input_stop(void* handle);
ZEUS_MA_EXPORT uint32_t zeus_ma_input_sample_rate(void* handle);
ZEUS_MA_EXPORT uint32_t zeus_ma_input_channels(void* handle);
ZEUS_MA_EXPORT void     zeus_ma_input_destroy(void* handle);

/* ---------------- Device enumeration ---------------------------------- */

/* Returns an opaque snapshot handle. The *_id functions return a hex-encoded
 * opaque ma_device_id blob suitable for passing back to *_create_for_device().
 * The caller must copy returned strings before zeus_ma_devices_destroy(). */
ZEUS_MA_EXPORT void*    zeus_ma_devices_create(void);
ZEUS_MA_EXPORT uint32_t zeus_ma_devices_playback_count(void* snapshot);
ZEUS_MA_EXPORT uint32_t zeus_ma_devices_capture_count(void* snapshot);
ZEUS_MA_EXPORT const char* zeus_ma_devices_playback_id(void* snapshot, uint32_t index);
ZEUS_MA_EXPORT const char* zeus_ma_devices_capture_id(void* snapshot, uint32_t index);
ZEUS_MA_EXPORT const char* zeus_ma_devices_playback_name(void* snapshot, uint32_t index);
ZEUS_MA_EXPORT const char* zeus_ma_devices_capture_name(void* snapshot, uint32_t index);
ZEUS_MA_EXPORT int32_t zeus_ma_devices_playback_default(void* snapshot, uint32_t index);
ZEUS_MA_EXPORT int32_t zeus_ma_devices_capture_default(void* snapshot, uint32_t index);
ZEUS_MA_EXPORT void zeus_ma_devices_destroy(void* snapshot);

/* ---------------- Library probe --------------------------------------- */

/* Returns a static, NUL-terminated string of the form
 * "zeus-miniaudio 0.11.25" so the C# side can log the vendored version. */
ZEUS_MA_EXPORT const char* zeus_ma_version(void);

#ifdef __cplusplus
}
#endif

#endif /* ZEUS_MINIAUDIO_H */

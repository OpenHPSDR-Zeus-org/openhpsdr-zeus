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

ZEUS_MA_EXPORT int32_t  zeus_ma_output_start(void* handle);
ZEUS_MA_EXPORT int32_t  zeus_ma_output_stop(void* handle);
ZEUS_MA_EXPORT uint32_t zeus_ma_output_sample_rate(void* handle);
ZEUS_MA_EXPORT uint32_t zeus_ma_output_channels(void* handle);

/* Diagnostics — valid after a successful *_create(). The backend name is a
 * static string ("WASAPI" / "DirectSound" / "WinMM" / "Core Audio" / ...) so
 * the C# log can confirm which backend won the ma_device_init(NULL,...) auto
 * selection. buffer_frames * periods / sample_rate = device buffer latency in
 * seconds — a deep ~1.7 s WASAPI shared-mode buffer vs a ~20 ms low-latency
 * one is the whole #468 question. */
ZEUS_MA_EXPORT const char* zeus_ma_output_backend_name(void* handle);
ZEUS_MA_EXPORT uint32_t    zeus_ma_output_buffer_frames(void* handle);
ZEUS_MA_EXPORT uint32_t    zeus_ma_output_periods(void* handle);

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

ZEUS_MA_EXPORT int32_t  zeus_ma_input_start(void* handle);
ZEUS_MA_EXPORT int32_t  zeus_ma_input_stop(void* handle);
ZEUS_MA_EXPORT uint32_t zeus_ma_input_sample_rate(void* handle);
ZEUS_MA_EXPORT uint32_t zeus_ma_input_channels(void* handle);

/* Diagnostics — see the playback-side getters above for semantics. */
ZEUS_MA_EXPORT const char* zeus_ma_input_backend_name(void* handle);
ZEUS_MA_EXPORT uint32_t    zeus_ma_input_buffer_frames(void* handle);
ZEUS_MA_EXPORT uint32_t    zeus_ma_input_periods(void* handle);

ZEUS_MA_EXPORT void     zeus_ma_input_destroy(void* handle);

/* ---------------- Library probe --------------------------------------- */

/* Returns a static, NUL-terminated string of the form
 * "zeus-miniaudio 0.11.25" so the C# side can log the vendored version. */
ZEUS_MA_EXPORT const char* zeus_ma_version(void);

#ifdef __cplusplus
}
#endif

#endif /* ZEUS_MINIAUDIO_H */

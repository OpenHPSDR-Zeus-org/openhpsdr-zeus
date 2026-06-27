/* SPDX-License-Identifier: GPL-2.0-or-later
 *
 * Openhpsdr-Zeus — In-process VST3 host bridge.
 * C ABI consumed by Zeus.Plugins.Host.Audio.VstBridgeNative (P/Invoke).
 *
 * Stability contract: this header is the single source of truth for the
 * .NET ↔ native boundary. Adding new functions is forward-compatible;
 * removing or changing existing signatures REQUIRES bumping ZVST_ABI.
 * The .NET side checks the ABI on init and refuses on mismatch, so a
 * wire-format drift cannot silently corrupt audio.
 */

#ifndef OPENHPSDR_ZEUS_ZVST_H
#define OPENHPSDR_ZEUS_ZVST_H

#include <stdint.h>

/* Export the C ABI. The shared library is compiled with hidden
 * default visibility on Unix to keep vst3sdk's internals out of the
 * dylib's exported-symbol table; each entry point below is then
 * re-exported explicitly. Windows uses __declspec(dllexport). */
#if defined(_WIN32) || defined(__CYGWIN__)
#  define ZVST_EXPORT __declspec(dllexport)
#elif defined(__GNUC__) || defined(__clang__)
#  define ZVST_EXPORT __attribute__((visibility("default")))
#else
#  define ZVST_EXPORT
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Bridge ABI version. Bump when any function below changes shape.
 * v2 added the editor (IPlugView) entry points at the end of this
 * header: zvst_editor_open / zvst_editor_close / zvst_editor_is_open.
 * v3 added zvst_get_latency_samples (end of header) and the
 * ZVST_UNSUPPORTED_PRECISION status. Existing signatures are unchanged;
 * v3 also makes zvst_load_vst3 bridge a host/plug-in channel-count
 * mismatch internally (mono host <-> stereo-only plug-in, and the
 * reverse) — the host I/O geometry stays exactly `channels` either way. */
#define ZVST_ABI 3

/* Status codes — must match VstBridgeStatus in C#. */
typedef enum zvst_status_t {
    ZVST_OK                    = 0,
    ZVST_ABI_MISMATCH          = 1,
    ZVST_FILE_NOT_FOUND        = 2,
    ZVST_NOT_A_VST3            = 3,
    ZVST_NO_AUDIO_EFFECT_CLASS = 4,
    ZVST_ACTIVATE_FAILED       = 5,
    ZVST_INVALID_HANDLE        = 6,
    ZVST_INVALID_ARGUMENTS     = 7,
    ZVST_NOT_IMPLEMENTED       = 8,
    ZVST_UNSUPPORTED_PRECISION = 9,  /* plug-in refuses 32-bit float processing */
    ZVST_OTHER                 = 255
} zvst_status_t;

/* Opaque plugin handle. The .NET side treats this as a void* / nint. */
typedef void* zvst_handle_t;

/*
 * Initialise the bridge. abi MUST equal ZVST_ABI; the bridge returns
 * ZVST_ABI_MISMATCH otherwise. Idempotent: safe to call multiple times
 * from independent loaders.
 */
ZVST_EXPORT int32_t zvst_init(int32_t abi);

/*
 * Load a VST3 plugin from `path` and prepare it to process audio at
 * the supplied geometry. On success, *out_handle is set to a non-NULL
 * value and the return is ZVST_OK.
 *
 * `path` is a UTF-8 absolute path to either a .vst3 bundle directory
 * (the common case) or a single .vst3 file (some flat-file Linux
 * builds). `channels` is 1 or 2 and describes the HOST buffer geometry
 * passed to zvst_process; `sample_rate` 44100..192000; `block_size`
 * 32..4096.
 *
 * Channel bridging (v3): if the plug-in cannot accept `channels`
 * directly (e.g. a stereo-only effect with a mono host, or a mono-only
 * effect with a stereo host) the bridge negotiates the plug-in's
 * supported 1- or 2-channel arrangement and up/down-mixes inside
 * zvst_process. The host always passes and receives exactly `channels`;
 * the conversion is unity-gain and reversible (mono duplicated to both
 * sides; stereo summed back as (L+R)/2). Load fails ZVST_ACTIVATE_FAILED
 * if the plug-in needs more than 2 channels.
 *
 * The handle is owned by the bridge until zvst_unload is called.
 */
ZVST_EXPORT int32_t zvst_load_vst3(
    const char* path,
    int32_t channels,
    int32_t sample_rate,
    int32_t block_size,
    zvst_handle_t* out_handle);

/*
 * Process `frames` of audio. `input` and `output` are planar float32
 * buffers of length channels * frames (channel-major layout — channel
 * 0's frames first, then channel 1's). In-place call (input == output)
 * is permitted.
 *
 * Realtime contract: this function MUST NOT allocate, lock, or
 * perform IO. If the plugin internally violates this contract, the
 * operator sees a glitch but the host stays up.
 */
ZVST_EXPORT int32_t zvst_process(
    zvst_handle_t handle,
    const float* input,
    float* output,
    int32_t frames);

/*
 * Set parameter `param_id` to `normalized` (clamped to [0,1] by the
 * bridge). Safe to call from the control thread; the VST3 controller
 * is required by spec to be reentrant relative to the audio thread.
 */
ZVST_EXPORT int32_t zvst_set_param(
    zvst_handle_t handle,
    uint32_t param_id,
    double normalized);

/*
 * Release the loaded plugin. The handle is invalid after this call.
 * Idempotent on a NULL handle (returns ZVST_OK).
 */
ZVST_EXPORT int32_t zvst_unload(zvst_handle_t handle);

/*
 * Release any process-wide bridge resources. Safe to call multiple
 * times; matched call counting against zvst_init.
 */
ZVST_EXPORT int32_t zvst_shutdown(void);

/*
 * Enumerate the audio-effect classes inside a VST3 file/bundle WITHOUT
 * activating them — the in-process engine's plugin scanner. `path` is a
 * UTF-8 absolute path to a .vst3 bundle or flat file. On success writes a
 * UTF-8 JSON array into `out_json` (always NUL-terminated, truncated to
 * `out_cap - 1` bytes), and sets *out_len (if non-NULL) to the FULL length
 * the JSON needed — so a caller that got truncated can re-call with a
 * bigger buffer. Each element is an object:
 *   {"uid":"...","name":"...","category":"...","vendor":"..."}
 * where `uid` is the class TUID string (the identifier to pass back to
 * load that exact sub-plugin from a shell file). Returns ZVST_OK even when
 * the file has zero audio-effect classes (empty array), or a load error
 * (ZVST_FILE_NOT_FOUND / ZVST_NOT_A_VST3). Forward-compatible addition
 * under ABI v2 — a new entry point only; no existing signature changed.
 */
ZVST_EXPORT int32_t zvst_describe(
    const char* path,
    char* out_json,
    int32_t out_cap,
    int32_t* out_len);

/* --- Editor (plug-in GUI) — ABI v2 -----------------------------------
 *
 * Open the plug-in's native editor (IPlugView) in a dedicated UI thread
 * owned by the bridge. The editor appears as a top-level OS window on
 * the host machine's desktop — exactly like a standalone VST host opens
 * a plug-in window. `title` is a UTF-8 window caption (typically the
 * plug-in display name); may be NULL.
 *
 * The window and all IPlugView calls live on the bridge's editor thread
 * (with its own message pump), so this is safe to call from the .NET
 * control thread while audio runs on the realtime thread.
 *
 * Idempotent: a second open while the editor is already up returns
 * ZVST_OK. Windows-only for now; other platforms return
 * ZVST_NOT_IMPLEMENTED. zvst_unload auto-closes an open editor.
 */
ZVST_EXPORT int32_t zvst_editor_open(zvst_handle_t handle, const char* title);

/*
 * Close the editor window if open. Blocks until the editor UI thread
 * has detached the view and torn the window down. Idempotent.
 */
ZVST_EXPORT int32_t zvst_editor_close(zvst_handle_t handle);

/*
 * Returns 1 if the editor window is currently open, 0 otherwise
 * (including on a NULL handle or a platform without editor support).
 * NOTE: this is a boolean, NOT a zvst_status_t.
 */
ZVST_EXPORT int32_t zvst_editor_is_open(zvst_handle_t handle);

/* --- Latency reporting — ABI v3 --------------------------------------
 *
 * Return the plug-in's reported processing latency in samples at the
 * geometry it was loaded with (IAudioProcessor::getLatencySamples),
 * captured once at load. 0 for a zero-latency effect, a NULL handle, or
 * a plug-in that does not report latency. Used by the host to surface
 * the total VST-insert latency of a chain; it is NOT a zvst_status_t.
 *
 * Forward-compatible addition — a new entry point only; no existing
 * signature changed.
 */
ZVST_EXPORT int32_t zvst_get_latency_samples(zvst_handle_t handle);

#ifdef __cplusplus
}
#endif

#endif /* OPENHPSDR_ZEUS_ZVST_H */

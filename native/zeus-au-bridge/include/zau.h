/* SPDX-License-Identifier: GPL-2.0-or-later
 *
 * Openhpsdr-Zeus — In-process Audio Unit (AUv2) host bridge.
 * C ABI consumed by Zeus.Plugins.Host.Audio.AuBridgeNative (P/Invoke).
 *
 * This header mirrors native/zeus-vst-bridge/include/zvst.h one-for-one:
 * same status-enum shape, same opaque-handle model, same planar float32
 * channel-major process contract, same normalized-[0,1] parameter API.
 * The .NET side reuses the SAME IVstBridgeNative seam and VstBridgeStatus
 * codes — AuBridgeNative is just a second implementation pointed at this
 * library. The only behavioural difference from the VST3 bridge is the
 * load identity: a VST3 loads from a filesystem path, whereas an Audio
 * Unit is resolved from the OS AudioComponent registry by its 12-byte
 * type/subtype/manufacturer four-char-code triple.
 *
 * macOS-ONLY. This library is built only inside an APPLE CMake block and
 * ships only under runtimes/osx-{x64,arm64}/native. On Windows/Linux the
 * managed loader finds nothing and AuBridgeNative degrades to clean
 * passthrough — exactly like VstBridgeNative does when its dylib is absent.
 *
 * Stability contract: this header is the single source of truth for the
 * .NET ↔ native boundary. Adding new functions is forward-compatible;
 * removing or changing existing signatures REQUIRES bumping ZAU_ABI.
 */

#ifndef OPENHPSDR_ZEUS_ZAU_H
#define OPENHPSDR_ZEUS_ZAU_H

#include <stdint.h>

/* Export the C ABI with hidden default visibility on Unix; each entry
 * point below is re-exported explicitly so the AudioToolbox internals
 * stay out of the dylib's exported-symbol table. */
#if defined(_WIN32) || defined(__CYGWIN__)
#  define ZAU_EXPORT __declspec(dllexport)
#elif defined(__GNUC__) || defined(__clang__)
#  define ZAU_EXPORT __attribute__((visibility("default")))
#else
#  define ZAU_EXPORT
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Bridge ABI version. Mirrors ZVST_ABI's role for the AU bridge.
 * v1: init / load / process / set_param / unload / shutdown.
 * v2: additive editor entry points — zau_editor_open / zau_editor_close /
 *     zau_editor_is_open. These host the AU's native Cocoa view (the vendor
 *     GUI, e.g. Waves) with an AUGenericView parameter-editor fallback in a
 *     bridge-owned NSWindow. All existing v1 signatures are unchanged, so the
 *     bump is forward-compatible; the .NET AuBridgeAbi.Current must match. */
#define ZAU_ABI 2

/* Status codes — must match VstBridgeStatus in C# (shared with the VST3
 * bridge). The names below map onto the same integer values; the AU
 * bridge reuses ZAU_NOT_AN_AU / ZAU_NO_AUDIO_EFFECT_CLASS for the
 * "component not found / not an effect" cases so the .NET side needs no
 * new status surface. */
typedef enum zau_status_t {
    ZAU_OK                    = 0,
    ZAU_ABI_MISMATCH          = 1,
    ZAU_FILE_NOT_FOUND        = 2,  /* AU: component not found in registry */
    ZAU_NOT_AN_AU             = 3,  /* AU: identifier malformed / wrong type */
    ZAU_NO_AUDIO_EFFECT_CLASS = 4,  /* AU: found, but not an 'aufx' effect  */
    ZAU_ACTIVATE_FAILED       = 5,
    ZAU_INVALID_HANDLE        = 6,
    ZAU_INVALID_ARGUMENTS     = 7,
    ZAU_NOT_IMPLEMENTED       = 8,
    ZAU_OTHER                 = 255
} zau_status_t;

/* Opaque plugin handle. The .NET side treats this as a void* / nint. */
typedef void* zau_handle_t;

/*
 * Initialise the bridge. abi MUST equal ZAU_ABI; the bridge returns
 * ZAU_ABI_MISMATCH otherwise. Idempotent: safe to call multiple times
 * from independent loaders.
 */
ZAU_EXPORT int32_t zau_init(int32_t abi);

/*
 * Load an Audio Unit effect and prepare it to process audio at the
 * supplied geometry. On success, *out_handle is set to a non-NULL value
 * and the return is ZAU_OK.
 *
 * `identifier` is a UTF-8 string of the form "type:subtype:manufacturer"
 * where each field is a four-character code (e.g. "aufx:lpas:appl" for
 * Apple's AULowpass). This is the AU analogue of the VST3 path/uid: the
 * 12-byte AudioComponentDescription triple is the stable identity the
 * scanner persists in the manifest. `type` is an insert-effect type —
 * 'aufx' (kAudioUnitType_Effect) or 'aumf' (kAudioUnitType_MusicEffect,
 * an effect that also takes MIDI, which Logic exposes in insert slots);
 * any other type is rejected with ZAU_NO_AUDIO_EFFECT_CLASS.
 *
 * `channels` is 1 or 2; `sample_rate` 44100..192000; `block_size`
 * 32..4096. The handle is owned by the bridge until zau_unload.
 */
ZAU_EXPORT int32_t zau_load(
    const char* identifier,
    int32_t channels,
    int32_t sample_rate,
    int32_t block_size,
    zau_handle_t* out_handle);

/*
 * Process `frames` of audio. `input` and `output` are planar float32
 * buffers of length channels * frames (channel-major layout — channel
 * 0's frames first, then channel 1's). In-place call (input == output)
 * is permitted.
 *
 * Realtime contract: this function MUST NOT allocate, lock, or perform
 * IO. All scratch is pre-sized at load. If the AU internally violates
 * the contract the operator sees a glitch but the host stays up; a hard
 * render failure soft-fails to memcpy passthrough and returns ZAU_OTHER.
 */
ZAU_EXPORT int32_t zau_process(
    zau_handle_t handle,
    const float* input,
    float* output,
    int32_t frames);

/*
 * Set parameter `param_id` to `normalized` (clamped to [0,1] by the
 * bridge, then scaled to the AU parameter's real [min,max] range).
 * Audio Units carry real-valued parameter ranges rather than the VST3
 * normalized convention, so the bridge owns the scaling — the .NET side
 * speaks the same normalized [0,1] language as the VST3 bridge.
 * Safe to call from the control thread.
 */
ZAU_EXPORT int32_t zau_set_param(
    zau_handle_t handle,
    uint32_t param_id,
    double normalized);

/*
 * Release the loaded AU. The handle is invalid after this call.
 * Idempotent on a NULL handle (returns ZAU_OK).
 */
ZAU_EXPORT int32_t zau_unload(zau_handle_t handle);

/*
 * Release any process-wide bridge resources. Safe to call multiple
 * times; matched call counting against zau_init.
 */
ZAU_EXPORT int32_t zau_shutdown(void);

/*
 * Enumerate installed AUv2 insert-effect components ('aufx' Effect and
 * 'aumf' MusicEffect — the types Logic exposes in insert slots) into a caller
 * provided UTF-8 buffer, one component per line, newline-separated, in
 * the form:
 *
 *   "type:subtype:manufacturer\tName\tManufacturerName\n"
 *
 * where the first field is the load identifier consumed by zau_load and
 * the trailing fields are display strings. Only components whose Mach-O
 * slice matches the host process architecture are returned (an in-process
 * host cannot dlopen a mismatched-arch AU), so the .NET side does not need
 * to arch-filter.
 *
 * `buffer` may be NULL to query the required size. On return *out_len is
 * set to the number of UTF-8 bytes written (or required, if buffer was
 * NULL or too small). Returns ZAU_OK on success, ZAU_INVALID_ARGUMENTS if
 * out_len is NULL. The result is plain text so the .NET side needs no
 * struct-marshalling ABI surface — purely additive.
 */
ZAU_EXPORT int32_t zau_enumerate_effects(
    char* buffer,
    int32_t buffer_len,
    int32_t* out_len);

/*
 * Open the loaded AU's native editor GUI in a bridge-owned NSWindow titled
 * with `title_utf8` (the plugin display name). The bridge first tries the
 * AU's vendor Cocoa view (kAudioUnitProperty_CocoaUI — what Waves and most
 * third-party AUs ship); if that property is absent or instantiation fails
 * it falls back to CoreAudioKit's AUGenericView, which yields an editable
 * parameter GUI for ANY AU, so "edit settings" is always satisfied.
 *
 * THREADING: all AppKit/window work runs on the process main thread (the
 * desktop host's AppKit run loop). This call validates the handle
 * synchronously then dispatches window creation to the main queue and
 * returns ZAU_OK optimistically (the generic-view fallback guarantees a
 * usable editor); the actual visible state is reported by
 * zau_editor_is_open. Idempotent — a second open while one is up returns
 * ZAU_OK. Returns ZAU_INVALID_HANDLE on a NULL handle or unloaded unit.
 * MUST NOT be called from the realtime render path.
 */
ZAU_EXPORT int32_t zau_editor_open(zau_handle_t handle, const char* title_utf8);

/*
 * Close the loaded AU's editor window if open. Idempotent (ZAU_OK when no
 * window is up). Runs the AppKit teardown on the main thread and blocks
 * until the window has been torn down, honouring the .NET EditorClose
 * "blocks until the editor UI thread has torn down" contract. MUST NOT be
 * called from the realtime render path.
 */
ZAU_EXPORT int32_t zau_editor_close(zau_handle_t handle);

/*
 * Returns 1 if a live editor window is currently open for `handle`, 0
 * otherwise (including a NULL/invalid handle). Lock-free atomic read — safe
 * to poll from the .NET control thread; never dispatches or blocks.
 */
ZAU_EXPORT int32_t zau_editor_is_open(zau_handle_t handle);

#ifdef __cplusplus
}
#endif

#endif /* OPENHPSDR_ZEUS_ZAU_H */

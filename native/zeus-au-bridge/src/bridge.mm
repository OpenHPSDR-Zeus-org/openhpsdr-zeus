// SPDX-License-Identifier: GPL-2.0-or-later
//
// Openhpsdr-Zeus Audio Unit (AUv2) host bridge.
//
// Hosts a single AUv2 effect in-process via the C AudioToolbox /
// AudioUnit API and runs it on the .NET side's realtime audio thread
// through the C ABI in include/zau.h. This mirrors the VST3 bridge
// (native/zeus-vst-bridge/src/bridge.cpp) one-for-one: same status
// codes, same opaque-handle ownership, same planar float32 channel-major
// process contract, same normalized-[0,1] parameter API.
//
// AUv2 specifics handled here:
//   - load identity is the OS AudioComponent registry (type/subtype/
//     manufacturer four-char-codes), NOT a filesystem path;
//   - the host drives the AU with AudioUnitRender + an input render
//     callback that hands the AU our pre-pointed planar buffers;
//   - non-interleaved float32 stream format on both scopes so the
//     channel-major layout maps 1:1 with no interleave shuffle;
//   - parameters are real-valued (min/max), so zau_set_param scales the
//     incoming normalized [0,1] value into the AU's parameter range.
//
// Threading model mirrors the VST3 bridge: zau_init / load / unload /
// set_param run on the .NET control thread; zau_process runs on the
// realtime audio thread. The loaded-plugin state is owned by the handle
// returned to .NET, which serialises access (no parallel process/unload).

#include "zau.h"

#import <AudioToolbox/AudioToolbox.h>
#import <CoreFoundation/CoreFoundation.h>
#import <Foundation/Foundation.h>

// Editor hosting (AUv2 Cocoa view + AUGenericView fallback in an NSWindow) is
// macOS-only and pulls in AppKit + CoreAudioKit. The whole bridge already only
// compiles on Apple, but per the bridge's hard cross-platform rule every new
// editor code path is explicitly guarded so the intent is unmistakable and a
// hypothetical non-Apple compile of this .mm never touches AppKit.
#if defined(__APPLE__)
#import <AppKit/AppKit.h>
#import <CoreAudioKit/CoreAudioKit.h>   // AUGenericView (always-works fallback)
#import <AudioUnit/AUCocoaUIView.h>     // AUCocoaUIBase protocol (vendor views)
#endif

#include <atomic>
#include <cstring>
#include <mutex>
#include <set>
#include <string>
#include <vector>

namespace {

std::atomic<int> g_init_count{0};

#if defined(__APPLE__)
// --- Live-handle registry (macOS-only; guards the editor's fire-and-forget
// main-queue window build against a use-after-free). zau_editor_open dispatches
// the AppKit window build to the main thread ASYNCHRONOUSLY and returns
// immediately; if the operator then unloads the plugin before the run loop
// drains that block, zau_unload would teardown+delete the LoadedAu while the
// queued block still holds the raw pointer, and draining it later would
// dereference freed memory. Every load registers its handle here and every
// unload removes it UNDER THE LOCK before delete; the queued build re-validates
// membership (holding the lock for its whole body) and bails if the handle is
// gone — so a freed handle is never dereferenced. Lookups compare the pointer
// VALUE only (a stale key is compared, never dereferenced), which is safe even
// if the pointee has already been freed. ---
struct LoadedAu; // forward decl: only the pointer type is needed here
std::mutex g_registry_mutex;
std::set<LoadedAu*> g_registry;
#endif

// Parse one four-character code from up to 4 bytes of `s`. Short codes are
// space-padded on the right (the CoreAudio convention). Returns the OSType
// in big-endian char order ('aufx' => 'a'<<24 | 'u'<<16 | 'f'<<8 | 'x').
static bool parse_fourcc(const std::string& s, OSType* out) {
    if (s.empty() || s.size() > 4) return false;
    char c[4] = {' ', ' ', ' ', ' '};
    for (size_t i = 0; i < s.size(); ++i) c[i] = s[i];
    *out = (static_cast<OSType>(static_cast<unsigned char>(c[0])) << 24) |
           (static_cast<OSType>(static_cast<unsigned char>(c[1])) << 16) |
           (static_cast<OSType>(static_cast<unsigned char>(c[2])) << 8)  |
           (static_cast<OSType>(static_cast<unsigned char>(c[3])));
    return true;
}

// Split "type:subtype:manufacturer" into its three four-char-code fields.
static bool parse_identifier(const char* identifier,
                             OSType* type, OSType* subtype, OSType* mfr) {
    if (!identifier) return false;
    std::string s(identifier);
    auto p1 = s.find(':');
    if (p1 == std::string::npos) return false;
    auto p2 = s.find(':', p1 + 1);
    if (p2 == std::string::npos) return false;
    std::string t  = s.substr(0, p1);
    std::string st = s.substr(p1 + 1, p2 - p1 - 1);
    std::string mf = s.substr(p2 + 1);
    return parse_fourcc(t, type) && parse_fourcc(st, subtype) && parse_fourcc(mf, mfr);
}

// Per-handle state. Owns one AudioUnit instance plus the pre-sized scratch
// the realtime path needs so zau_process never allocates.
struct LoadedAu {
    AudioComponentInstance unit{nullptr};

    // `channels` is the CALLER-facing channel count (the .NET contract: 1 or
    // 2). `au_channels` is what the AU itself was negotiated to run at —
    // usually equal, but Waves ships fixed mono- or stereo-ONLY components
    // that reject a mismatched stream format with -10868
    // (kAudioUnitErr_FormatNotSupported). When they differ the realtime path
    // up/down-mixes between the two through au_out_scratch (no allocation).
    int32_t channels{1};
    int32_t au_channels{1};
    int32_t sample_rate{48000};
    int32_t block_size{256};

    // Input buffer-list reused across render calls. Channel pointers are
    // re-pointed at the caller's planar buffer on every zau_process call;
    // we never copy. Storage is sized at load for au_channels buffers.
    std::vector<uint8_t> input_abl_storage; // AudioBufferList + N AudioBuffer
    const float* current_input{nullptr};    // set per process() call
    int32_t      current_frames{0};

    // Realtime up/down-mix scratch, sized once at load. au_out_scratch holds
    // the AU's au_channels-wide render output before it is mixed to the
    // caller's channel count; au_in_scratch holds the mono average when a
    // multi-channel caller feeds a mono-only AU. Both stay empty (and the
    // path stays zero-copy) when au_channels == channels. Touched solely by
    // the audio thread.
    std::vector<float> au_out_scratch;
    std::vector<float> au_in_scratch;

    // Monotonic host sample-time cursor handed to AudioUnitRender. It MUST
    // advance by `frames` every block: time-based AUs (reverb / delay /
    // chorus / tremolo and any LFO-driven effect) read the render timestamp
    // as their clock. Pinning it to 0 every block — as this bridge originally
    // did — makes those effects reset or stutter at every block boundary
    // (audible as garbled / modulated output), even though a static filter
    // like AULowpass is unaffected. Owned by the realtime thread; the AU
    // serialises process/unload via the handle so no atomics are needed.
    Float64 render_sample_time{0.0};

#if defined(__APPLE__)
    // --- Editor state. Touched ONLY on the main thread (where all AppKit
    // calls run) except `editor_open`, which is a lock-free flag the .NET
    // control thread polls via zau_editor_is_open. ARC is OFF (see
    // CMakeLists), so every Obj-C object here is manually retained on store
    // and released on teardown; the NSWindow has releasedWhenClosed = NO so
    // the bridge owns its lifetime explicitly. `id` typed to avoid leaking
    // AppKit/CoreAudioKit types into the non-editor struct surface. ---
    NSWindow* editor_window{nullptr}; // bridge-owned host window
    NSView*   editor_view{nullptr};   // vendor Cocoa view or AUGenericView
    id        editor_factory{nil};    // id<AUCocoaUIBase>; nil for AUGenericView
    NSBundle* editor_bundle{nullptr}; // kept loaded while a vendor view lives
    id        editor_delegate{nil};   // ZauEditorWindowDelegate (NSWindowDelegate)
    std::atomic<bool> editor_open{false};
    std::string editor_title;
#endif

    AudioBufferList* input_abl() {
        return reinterpret_cast<AudioBufferList*>(input_abl_storage.data());
    }
};

// Input render callback: the AU pulls its input from us here. We hand it
// pointers into the caller's planar buffer for this block. No allocation,
// no locking — pure pointer arithmetic on pre-sized storage.
static OSStatus zau_input_callback(void* inRefCon,
                                   AudioUnitRenderActionFlags* /*ioActionFlags*/,
                                   const AudioTimeStamp* /*inTimeStamp*/,
                                   UInt32 /*inBusNumber*/,
                                   UInt32 inNumberFrames,
                                   AudioBufferList* ioData) {
    auto* p = static_cast<LoadedAu*>(inRefCon);
    if (!p || !ioData || !p->current_input) return kAudioUnitErr_InvalidParameter;

    const int auch  = p->au_channels;             // channels the AU expects
    const int cch   = p->channels;                // channels the caller gave
    const UInt32 frames = inNumberFrames;          // == p->current_frames
    const size_t stride = static_cast<size_t>(p->current_frames);
    const float* in = p->current_input;            // caller planar, stride `stride`
    const int nbuf  = static_cast<int>(ioData->mNumberBuffers);

    if (auch == cch) {
        // Matched count — zero-copy fast path. Point each AU input buffer at
        // the caller's planar slice (channel c starts at c*stride).
        for (int c = 0; c < auch && c < nbuf; ++c) {
            ioData->mBuffers[c].mNumberChannels = 1;
            ioData->mBuffers[c].mDataByteSize   = frames * sizeof(float);
            ioData->mBuffers[c].mData = const_cast<float*>(in + static_cast<size_t>(c) * stride);
        }
    } else if (cch == 1) {
        // Up-mix mono caller -> multi-channel AU: every AU input buffer reads
        // the SAME mono slice (L=R). Read-only aliasing, no copy.
        for (int c = 0; c < auch && c < nbuf; ++c) {
            ioData->mBuffers[c].mNumberChannels = 1;
            ioData->mBuffers[c].mDataByteSize   = frames * sizeof(float);
            ioData->mBuffers[c].mData = const_cast<float*>(in);
        }
    } else {
        // Down-mix multi-channel caller -> mono AU (auch == 1): average the
        // caller channels into the pre-sized mono input scratch.
        float* dst = p->au_in_scratch.data();
        for (UInt32 f = 0; f < frames; ++f) {
            float acc = 0.0f;
            for (int c = 0; c < cch; ++c) acc += in[static_cast<size_t>(c) * stride + f];
            dst[f] = acc / static_cast<float>(cch);
        }
        if (nbuf > 0) {
            ioData->mBuffers[0].mNumberChannels = 1;
            ioData->mBuffers[0].mDataByteSize   = frames * sizeof(float);
            ioData->mBuffers[0].mData = dst;
        }
    }
    return noErr;
}

// Build a canonical non-interleaved float32 ASBD for `channels`.
static AudioStreamBasicDescription make_asbd(int channels, double sample_rate) {
    AudioStreamBasicDescription asbd{};
    asbd.mSampleRate       = sample_rate;
    asbd.mFormatID         = kAudioFormatLinearPCM;
    asbd.mFormatFlags      = kAudioFormatFlagIsFloat
                           | kAudioFormatFlagIsPacked
                           | kAudioFormatFlagIsNonInterleaved;
    asbd.mFramesPerPacket  = 1;
    asbd.mBytesPerPacket   = sizeof(float);   // per channel, non-interleaved
    asbd.mBytesPerFrame    = sizeof(float);   // per channel, non-interleaved
    asbd.mChannelsPerFrame = static_cast<UInt32>(channels);
    asbd.mBitsPerChannel   = 8 * sizeof(float);
    return asbd;
}

// Does one AUChannelInfo entry permit running `count` channels in AND out?
// A negative side (-1 "any", -2 "any, must match the other side") accepts any
// count; a non-negative side accepts only an exact match. For our equal-in/out
// insert-effect use this is sufficient: a wildcard entry ([-1,-1]) accepts the
// caller's count, while a fixed entry ([2,2]) accepts only that count.
static bool channel_entry_supports(const AUChannelInfo& e, int count) {
    auto side_ok = [count](SInt16 v) { return v < 0 || v == static_cast<SInt16>(count); };
    return side_ok(e.inChannels) && side_ok(e.outChannels);
}

// Negotiate the channel count the AU will actually run at. Waves ships
// FIXED-channel components (separate mono/stereo variants) that report a rigid
// kAudioUnitProperty_SupportedNumChannels and reject a mismatched stream
// format with -10868 (kAudioUnitErr_FormatNotSupported). Apple AUs report
// wildcards and accept whatever we set — which is why AUReverb2 always loads at
// the caller's count and the Waves AUs do not. Strategy: honour the caller's
// requested count if the AU supports it; otherwise prefer a stereo (2) fixed
// layout, then mono (1); if the property is absent the AU is flexible and we
// keep the caller's count (today's correct path for Apple AUs).
static int negotiate_au_channels(AudioComponentInstance unit, int requested) {
    UInt32 sz = 0;
    OSStatus st = AudioUnitGetPropertyInfo(unit, kAudioUnitProperty_SupportedNumChannels,
                                           kAudioUnitScope_Global, 0, &sz, nullptr);
    if (st != noErr || sz < sizeof(AUChannelInfo))
        return requested; // flexible / property absent — honour caller's count

    int n = static_cast<int>(sz / sizeof(AUChannelInfo));
    std::vector<AUChannelInfo> infos(static_cast<size_t>(n));
    st = AudioUnitGetProperty(unit, kAudioUnitProperty_SupportedNumChannels,
                              kAudioUnitScope_Global, 0, infos.data(), &sz);
    if (st != noErr) return requested;

    for (const auto& e : infos) if (channel_entry_supports(e, requested)) return requested;
    for (const auto& e : infos) if (channel_entry_supports(e, 2)) return 2;
    for (const auto& e : infos) if (channel_entry_supports(e, 1)) return 1;
    return requested; // nothing matched — let the format set/initialise fail loudly
}

static void teardown(LoadedAu& p) {
    if (p.unit) {
        AudioUnitUninitialize(p.unit);
        AudioComponentInstanceDispose(p.unit);
        p.unit = nullptr;
    }
}

// Resolve, instantiate, configure, and initialise the AU for p's geometry.
// Returns ZAU_OK or a failure status. No realtime work here.
static int do_load(LoadedAu& p, const char* identifier) {
    OSType type = 0, subtype = 0, mfr = 0;
    if (!parse_identifier(identifier, &type, &subtype, &mfr))
        return ZAU_NOT_AN_AU;

    // We host insert effects: 'aufx' (Effect) and 'aumf' (MusicEffect, an
    // effect that also accepts MIDI — Logic exposes these in insert slots).
    // Both render through AudioUnitRender identically. Any other type
    // (instruments, generators, mixers, ...) is rejected with the same status
    // the VST3 bridge uses for "no audio effect class".
    if (type != kAudioUnitType_Effect && type != kAudioUnitType_MusicEffect)
        return ZAU_NO_AUDIO_EFFECT_CLASS;

    AudioComponentDescription desc{};
    desc.componentType         = type;
    desc.componentSubType      = subtype;
    desc.componentManufacturer = mfr;
    desc.componentFlags        = 0;
    desc.componentFlagsMask    = 0;

    AudioComponent comp = AudioComponentFindNext(nullptr, &desc);
    if (!comp) return ZAU_FILE_NOT_FOUND; // component not present in registry

    OSStatus st = AudioComponentInstanceNew(comp, &p.unit);
    if (st != noErr || !p.unit) { p.unit = nullptr; return ZAU_ACTIVATE_FAILED; }

    // Negotiate the channel count the AU will actually run at. Fixed-channel
    // AUs (e.g. Waves stereo-only HEQS/LA2S, mono-only LA2M) reject a
    // mismatched stream format with -10868; honour the caller's count when the
    // AU is flexible, otherwise adopt the AU's fixed layout and up/down-mix in
    // the realtime path. `channels` stays the caller-facing count.
    p.au_channels = negotiate_au_channels(p.unit, p.channels);

    // Set the non-interleaved float32 stream format on both scopes (using the
    // AU's negotiated channel count) so the channel-major layout maps with no
    // interleave shuffle.
    AudioStreamBasicDescription asbd = make_asbd(p.au_channels, static_cast<double>(p.sample_rate));
    st = AudioUnitSetProperty(p.unit, kAudioUnitProperty_StreamFormat,
                              kAudioUnitScope_Input, 0, &asbd, sizeof(asbd));
    if (st != noErr) {
        // Log what the AU rejected on the wire (control thread, not realtime):
        // an OSStatus here is almost always -10868 (kAudioUnitErr_FormatNotSupported)
        // from a channel-count mismatch the negotiation above could not resolve.
        fprintf(stderr, "[zau] StreamFormat(input) rejected: OSStatus=%d "
                "(req_ch=%d au_ch=%d)\n", (int)st, p.channels, p.au_channels);
        teardown(p);
        return ZAU_ACTIVATE_FAILED;
    }
    st = AudioUnitSetProperty(p.unit, kAudioUnitProperty_StreamFormat,
                              kAudioUnitScope_Output, 0, &asbd, sizeof(asbd));
    if (st != noErr) {
        fprintf(stderr, "[zau] StreamFormat(output) rejected: OSStatus=%d "
                "(req_ch=%d au_ch=%d)\n", (int)st, p.channels, p.au_channels);
        teardown(p);
        return ZAU_ACTIVATE_FAILED;
    }

    // Bound the render block size so AudioUnitInitialize sizes internal
    // scratch for our worst case. Some AUs reject a slice larger than they
    // support; treat a failure here as a hard load failure (consistent with
    // the other property sets above) rather than silently initialising with
    // an unknown slice ceiling, which could let AudioUnitRender be driven past
    // the buffers it sized for.
    UInt32 maxFrames = static_cast<UInt32>(p.block_size);
    st = AudioUnitSetProperty(p.unit, kAudioUnitProperty_MaximumFramesPerSlice,
                              kAudioUnitScope_Global, 0, &maxFrames, sizeof(maxFrames));
    if (st != noErr) { teardown(p); return ZAU_ACTIVATE_FAILED; }

    // Wire the input render callback so the AU pulls our planar buffers.
    AURenderCallbackStruct cb{};
    cb.inputProc       = zau_input_callback;
    cb.inputProcRefCon = &p;
    st = AudioUnitSetProperty(p.unit, kAudioUnitProperty_SetRenderCallback,
                              kAudioUnitScope_Input, 0, &cb, sizeof(cb));
    if (st != noErr) { teardown(p); return ZAU_ACTIVATE_FAILED; }

    st = AudioUnitInitialize(p.unit);
    if (st != noErr) {
        fprintf(stderr, "[zau] AudioUnitInitialize failed: OSStatus=%d "
                "(req_ch=%d au_ch=%d)\n", (int)st, p.channels, p.au_channels);
        teardown(p);
        return ZAU_ACTIVATE_FAILED;
    }

    // Pre-size the render AudioBufferList scratch: one AudioBuffer per AU
    // channel (non-interleaved). AudioBufferList carries one mBuffers entry
    // inline, so add (au_channels-1) more.
    size_t ablBytes = sizeof(AudioBufferList) +
                      (p.au_channels > 0 ? (p.au_channels - 1) : 0) * sizeof(AudioBuffer);
    p.input_abl_storage.assign(ablBytes, 0);

    // Pre-size the up/down-mix scratch ONLY when the AU runs at a different
    // channel count than the caller — the matched-count path stays zero-copy.
    if (p.au_channels != p.channels) {
        size_t n = static_cast<size_t>(p.au_channels) * static_cast<size_t>(p.block_size);
        p.au_out_scratch.assign(n, 0.0f);
        p.au_in_scratch.assign(n, 0.0f);
    }

    return ZAU_OK;
}

} // namespace

// ===========================================================================
//  AUv2 editor hosting (macOS-only).
//
//  Hosts the AU's native GUI in a bridge-owned NSWindow: the vendor Cocoa view
//  (kAudioUnitProperty_CocoaUI — what Waves and most third-party AUs ship) with
//  CoreAudioKit's AUGenericView as the always-works fallback. ALL AppKit work
//  runs on the process main thread (the desktop host's AppKit run loop); the
//  realtime render path (zau_process / zau_input_callback) never touches any of
//  this. ARC is OFF, so every Obj-C object is released by hand; the window has
//  releasedWhenClosed = NO and the bridge owns its lifetime explicitly.
// ===========================================================================
#if defined(__APPLE__)

// Run an AppKit block on the main thread. Inline when already there (the core
// deadlock guard: a dispatch_sync to main from the main thread self-deadlocks).
static void zau_run_on_main_sync(dispatch_block_t block) {
    if ([NSThread isMainThread]) block();
    else dispatch_sync(dispatch_get_main_queue(), block);
}
static void zau_run_on_main_async(dispatch_block_t block) {
    if ([NSThread isMainThread]) block();
    else dispatch_async(dispatch_get_main_queue(), block);
}

// Detach + release every editor-owned Obj-C object. MAIN THREAD ONLY. Uses
// autorelease (not release) so it is safe to call from inside -windowWillose:
// while AppKit is mid-close on the window or while `self` (the delegate) is the
// running object — the actual deallocations defer to the end of the run loop.
// Idempotent: nulls the struct pointers up front so a second call is a no-op.
static void zau_editor_release_owned(LoadedAu* p) {
    if (!p) return;
    NSWindow* w   = p->editor_window;
    NSView*   v   = p->editor_view;
    id        fac = p->editor_factory;
    NSBundle* b   = p->editor_bundle;
    id        del = p->editor_delegate;

    p->editor_window   = nullptr;
    p->editor_view     = nullptr;
    p->editor_factory  = nil;
    p->editor_bundle   = nullptr;
    p->editor_delegate = nil;
    p->editor_open.store(false);

    if (w) {
        [w setDelegate:nil];     // stop further delegate callbacks
        [w setContentView:nil];  // drop the window's retain on the view
        [w autorelease];         // our +1 from alloc; deferred to run-loop end
    }
    if (v)   [v autorelease];
    if (fac) [fac autorelease];
    if (b)   [b autorelease];
    if (del) [del autorelease];  // may be the running delegate — autorelease
}

// NSWindowDelegate so the operator clicking the window's red close button is
// handled identically to a programmatic zau_editor_close.
@interface ZauEditorWindowDelegate : NSObject <NSWindowDelegate> {
@public
    LoadedAu* _p;
}
- (instancetype)initWithLoadedAu:(LoadedAu*)p;
@end

@implementation ZauEditorWindowDelegate
- (instancetype)initWithLoadedAu:(LoadedAu*)p {
    if ((self = [super init])) { _p = p; }
    return self;
}
- (void)windowWillClose:(NSNotification*)note {
    (void)note;
    // AppKit posts this on the main thread during -close. If we are already
    // tearing down explicitly (pointers nulled), there is nothing to do.
    if (_p && _p->editor_window) zau_editor_release_owned(_p);
}
@end

// Build and present the editor window. MAIN THREAD ONLY.
//
// Dispatched fire-and-forget by zau_editor_open, so by the time the run loop
// drains this block the handle may already have been unloaded+freed. The whole
// body runs UNDER g_registry_mutex and bails immediately if `p` is no longer a
// registered live handle: zau_unload erases the handle under the same lock
// before deleting it, so either (a) this block ran first and zau_unload's erase
// blocks until we finish, or (b) zau_unload erased first and we bail without
// touching freed memory. The membership test compares the pointer value only.
static void zau_editor_build_on_main(LoadedAu* p) {
    std::lock_guard<std::mutex> reg(g_registry_mutex);
    if (g_registry.find(p) == g_registry.end()) return; // handle freed/unloaded — bail
    @autoreleasepool {
        if (!p || !p->unit) return;
        if (p->editor_window) { p->editor_open.store(true); return; } // already up

        // Ensure an NSApplication exists (idempotent). NEVER call -run — the
        // desktop host (Photino) owns the main run loop.
        [NSApplication sharedApplication];

        NSView*   view    = nil;   // +1 owned by us once set
        id        factory = nil;   // +1 owned (vendor Cocoa factory) or nil
        NSBundle* bundle  = nil;   // +1 owned (vendor bundle) or nil

        // 1) Vendor Cocoa view (kAudioUnitProperty_CocoaUI) — what Waves ships.
        UInt32 sz = 0; Boolean writable = false;
        OSStatus st = AudioUnitGetPropertyInfo(p->unit, kAudioUnitProperty_CocoaUI,
                          kAudioUnitScope_Global, 0, &sz, &writable);
        if (st == noErr && sz >= sizeof(AudioUnitCocoaViewInfo)) {
            AudioUnitCocoaViewInfo* info = (AudioUnitCocoaViewInfo*)calloc(1, sz);
            if (info) {
                st = AudioUnitGetProperty(p->unit, kAudioUnitProperty_CocoaUI,
                         kAudioUnitScope_Global, 0, info, &sz);
                if (st == noErr && info->mCocoaAUViewBundleLocation) {
                    NSURL* url = (__bridge NSURL*)info->mCocoaAUViewBundleLocation;
                    NSString* cls = info->mCocoaAUViewClass[0]
                        ? (__bridge NSString*)info->mCocoaAUViewClass[0] : nil;
                    NSBundle* b = [NSBundle bundleWithURL:url]; // autoreleased
                    if (b && [b load] && cls) {
                        Class fc = [b classNamed:cls];
                        if (fc) {
                            NSObject<AUCocoaUIBase>* f = [[fc alloc] init]; // +1
                            if ([f respondsToSelector:@selector(uiViewForAudioUnit:withSize:)]) {
                                NSView* v = [f uiViewForAudioUnit:p->unit
                                                         withSize:NSZeroSize]; // autoreleased
                                if (v) {
                                    view    = [v retain]; // take our +1
                                    factory = f;          // keep the factory +1
                                    bundle  = [b retain];  // keep the bundle +1
                                } else {
                                    [f release];
                                }
                            } else {
                                [f release];
                            }
                        }
                    }
                }
                // The CocoaUI property hands back +1 CF references; release them.
                if (info->mCocoaAUViewBundleLocation)
                    CFRelease(info->mCocoaAUViewBundleLocation);
                UInt32 nClasses = (UInt32)((sz - sizeof(CFURLRef)) / sizeof(CFStringRef));
                for (UInt32 i = 0; i < nClasses; ++i)
                    if (info->mCocoaAUViewClass[i]) CFRelease(info->mCocoaAUViewClass[i]);
                free(info);
            }
        }

        // 2) Fallback — AUGenericView. Always yields an editable parameter GUI
        //    for ANY AU (incl. stereo-only Waves units), satisfying "edit
        //    settings" even when no vendor view is present.
        if (!view) {
            factory = nil;
            bundle  = nil;
            view = [[AUGenericView alloc] initWithAudioUnit:p->unit]; // +1
        }
        if (!view) return; // pathological: nothing to host

        // 3) Host the view in a titled, closable bridge-owned window.
        NSRect vf = view.frame;
        if (vf.size.width < 1.0 || vf.size.height < 1.0)
            vf = NSMakeRect(0, 0, 480, 320);
        NSRect frame = NSMakeRect(0, 0, vf.size.width, vf.size.height);
        NSUInteger style = NSWindowStyleMaskTitled
                         | NSWindowStyleMaskClosable
                         | NSWindowStyleMaskMiniaturizable;
        NSWindow* w = [[NSWindow alloc] initWithContentRect:frame
                                                  styleMask:style
                                                    backing:NSBackingStoreBuffered
                                                      defer:NO]; // +1
        w.releasedWhenClosed = NO; // bridge owns the lifetime
        if (!p->editor_title.empty()) {
            NSString* t = [NSString stringWithUTF8String:p->editor_title.c_str()];
            if (t) w.title = t;
        }
        w.contentView = view; // window takes its own retain on the view
        ZauEditorWindowDelegate* del =
            [[ZauEditorWindowDelegate alloc] initWithLoadedAu:p]; // +1
        w.delegate = del; // weak/assign
        [w center];
        [w makeKeyAndOrderFront:nil];
        [NSApp activateIgnoringOtherApps:YES];

        p->editor_window   = w;       // +1
        p->editor_view     = view;    // +1
        p->editor_factory  = factory; // +1 or nil
        p->editor_bundle   = bundle;  // +1 or nil
        p->editor_delegate = del;     // +1
        p->editor_open.store(true);
    } // @autoreleasepool
}

// Close the editor window if present. MAIN THREAD ONLY. -close fires
// windowWillClose: synchronously, which releases the owned objects.
static void zau_editor_close_on_main(LoadedAu* p) {
    @autoreleasepool {
        if (!p) return;
        if (p->editor_window) [p->editor_window close]; // -> windowWillClose:
        else                  zau_editor_release_owned(p); // straggler cleanup
    }
}

#endif // __APPLE__

extern "C" {

int32_t zau_init(int32_t abi) {
    if (abi != ZAU_ABI) return ZAU_ABI_MISMATCH;
    g_init_count.fetch_add(1);
    return ZAU_OK;
}

int32_t zau_load(
    const char* identifier,
    int32_t channels,
    int32_t sample_rate,
    int32_t block_size,
    zau_handle_t* out_handle)
{
    if (!identifier || !out_handle) return ZAU_INVALID_ARGUMENTS;
    if (channels < 1 || channels > 2) return ZAU_INVALID_ARGUMENTS;
    if (sample_rate < 44100 || sample_rate > 192000) return ZAU_INVALID_ARGUMENTS;
    if (block_size < 32 || block_size > 4096) return ZAU_INVALID_ARGUMENTS;

    auto* p = new LoadedAu();
    p->channels = channels;
    p->sample_rate = sample_rate;
    p->block_size = block_size;

    int status = do_load(*p, identifier);
    if (status != ZAU_OK) {
        teardown(*p);
        delete p;
        return status;
    }
#if defined(__APPLE__)
    // Register BEFORE handing the handle out so any subsequent zau_editor_open →
    // async build can validate liveness (see g_registry above). Paired with the
    // erase in zau_unload.
    {
        std::lock_guard<std::mutex> reg(g_registry_mutex);
        g_registry.insert(p);
    }
#endif
    *out_handle = static_cast<void*>(p);
    return ZAU_OK;
}

int32_t zau_process(
    zau_handle_t handle,
    const float* input,
    float* output,
    int32_t frames)
{
    if (!handle) return ZAU_INVALID_HANDLE;
    if (!input || !output) return ZAU_INVALID_ARGUMENTS;
    auto* p = static_cast<LoadedAu*>(handle);
    if (frames < 1 || frames > p->block_size) return ZAU_INVALID_ARGUMENTS;

    // Stage the caller's input for the render callback to hand to the AU.
    p->current_input  = input;
    p->current_frames = frames;

    const int auch = p->au_channels;
    const bool mismatched = (auch != p->channels);

    // Point the render AudioBufferList at the AU's au_channels output. When the
    // AU runs at the caller's count we render straight into the caller's planar
    // output (zero-copy); otherwise we render into au_out_scratch and mix down
    // below.
    float* au_out = mismatched ? p->au_out_scratch.data() : output;
    AudioBufferList* abl = p->input_abl();
    abl->mNumberBuffers = static_cast<UInt32>(auch);
    for (int c = 0; c < auch; ++c) {
        abl->mBuffers[c].mNumberChannels = 1;
        abl->mBuffers[c].mDataByteSize   = static_cast<UInt32>(frames) * sizeof(float);
        abl->mBuffers[c].mData = au_out + static_cast<size_t>(c) * frames;
    }

    AudioUnitRenderActionFlags flags = 0;
    AudioTimeStamp ts{};
    ts.mFlags       = kAudioTimeStampSampleTimeValid;
    ts.mSampleTime  = p->render_sample_time;

    OSStatus st = AudioUnitRender(p->unit, &flags, &ts, 0,
                                  static_cast<UInt32>(frames), abl);
    p->current_input = nullptr;

    // Mix the AU's au_channels output down/up to the caller's channel count.
    if (st == noErr && mismatched) {
        if (p->channels == 1) {
            // AU stereo -> caller mono: take L. Symmetric (L==R) processing of
            // a duplicated mono input makes take-L mono-safe (no phase
            // cancellation) for the TX-vocal / RX chain.
            std::memcpy(output, au_out, static_cast<size_t>(frames) * sizeof(float));
        } else {
            // AU mono -> caller stereo: duplicate the single AU channel to both
            // caller planar slices.
            std::memcpy(output, au_out, static_cast<size_t>(frames) * sizeof(float));
            std::memcpy(output + static_cast<size_t>(frames), au_out,
                        static_cast<size_t>(frames) * sizeof(float));
        }
    }
    // Advance the host clock by exactly the rendered frame count so the next
    // block continues seamlessly (monotonic, gap-free). Even on a soft render
    // failure we still advance: the slot emitted `frames` of passthrough audio,
    // so the AU's notion of elapsed time must match the audio that left.
    p->render_sample_time += static_cast<Float64>(frames);
    if (st != noErr) {
        // Soft fail — copy input to output. The .NET wrapper downgrades the
        // chain to pass-through (mirrors the VST3 bridge's ZVST_OTHER path).
        std::memcpy(output, input,
            static_cast<size_t>(p->channels) * static_cast<size_t>(frames) * sizeof(float));
        return ZAU_OTHER;
    }
    return ZAU_OK;
}

int32_t zau_set_param(
    zau_handle_t handle,
    uint32_t param_id,
    double normalized)
{
    if (!handle) return ZAU_INVALID_HANDLE;
    auto* p = static_cast<LoadedAu*>(handle);
    if (!p->unit) return ZAU_INVALID_HANDLE;

    if (normalized < 0.0) normalized = 0.0;
    if (normalized > 1.0) normalized = 1.0;

    // AU parameters carry real [min,max] ranges, unlike VST3's normalized
    // convention. Query the range and scale our normalized value into it so
    // the .NET side speaks the same [0,1] language for both backends.
    AudioUnitParameterInfo info{};
    UInt32 sz = sizeof(info);
    OSStatus st = AudioUnitGetProperty(p->unit, kAudioUnitProperty_ParameterInfo,
                                       kAudioUnitScope_Global,
                                       static_cast<AudioUnitParameterID>(param_id),
                                       &info, &sz);
    AudioUnitParameterValue value;
    if (st == noErr) {
        value = static_cast<AudioUnitParameterValue>(
            info.minValue + normalized * (info.maxValue - info.minValue));
    } else {
        // No range info — pass the normalized value through unscaled.
        value = static_cast<AudioUnitParameterValue>(normalized);
    }

    st = AudioUnitSetParameter(p->unit,
                               static_cast<AudioUnitParameterID>(param_id),
                               kAudioUnitScope_Global, 0, value, 0);
    return st == noErr ? ZAU_OK : ZAU_OTHER;
}

int32_t zau_unload(zau_handle_t handle) {
    if (!handle) return ZAU_OK;
    auto* p = static_cast<LoadedAu*>(handle);
#if defined(__APPLE__)
    // Remove from the live-handle registry FIRST, under the lock, so a queued
    // editor-build block (dispatched fire-and-forget by zau_editor_open) that
    // has not yet run will bail instead of dereferencing this about-to-be-freed
    // handle. A build block already mid-flight holds the lock, so this erase
    // blocks until it finishes — after which p->editor_window is set and the
    // close path below tears it down. Either way no freed pointer is touched.
    {
        std::lock_guard<std::mutex> reg(g_registry_mutex);
        g_registry.erase(p);
    }
    // Close the editor window (on the main thread) BEFORE disposing the unit:
    // a live Cocoa view over a disposed AudioUnit would crash. The handle
    // already serialises process-vs-unload, so no render runs concurrently.
    if (p->editor_open.load() || p->editor_window) {
        p->editor_open.store(false);
        zau_run_on_main_sync(^{ zau_editor_close_on_main(p); });
    }
#endif
    teardown(*p);
    delete p;
    return ZAU_OK;
}

int32_t zau_shutdown(void) {
    if (g_init_count.load() > 0) g_init_count.fetch_sub(1);
    return ZAU_OK;
}

// --- AUv2 editor entry points (ABI v2, additive). See editor machinery above.
int32_t zau_editor_open(zau_handle_t handle, const char* title_utf8) {
#if defined(__APPLE__)
    if (!handle) return ZAU_INVALID_HANDLE;
    auto* p = static_cast<LoadedAu*>(handle);
    if (!p->unit) return ZAU_INVALID_HANDLE;
    if (p->editor_open.load()) return ZAU_OK; // idempotent
    p->editor_title = title_utf8 ? title_utf8 : "";
    // Fire-and-forget on the main queue: the AUGenericView fallback guarantees a
    // usable editor materialises, so we return OK optimistically and let
    // zau_editor_is_open report the true visible state. Never blocks the caller.
    zau_run_on_main_async(^{ zau_editor_build_on_main(p); });
    return ZAU_OK;
#else
    (void)handle; (void)title_utf8;
    return ZAU_NOT_IMPLEMENTED;
#endif
}

int32_t zau_editor_close(zau_handle_t handle) {
#if defined(__APPLE__)
    if (!handle) return ZAU_OK;
    auto* p = static_cast<LoadedAu*>(handle);
    if (!p->editor_open.load() && !p->editor_window) return ZAU_OK; // idempotent
    // Flip the flag BEFORE teardown so pollers never see "open" mid-close, then
    // block (on the .NET control thread, not main) until the window is gone to
    // honour the EditorClose "blocks until torn down" contract.
    p->editor_open.store(false);
    zau_run_on_main_sync(^{ zau_editor_close_on_main(p); });
    return ZAU_OK;
#else
    (void)handle;
    return ZAU_OK;
#endif
}

int32_t zau_editor_is_open(zau_handle_t handle) {
#if defined(__APPLE__)
    if (!handle) return 0;
    auto* p = static_cast<LoadedAu*>(handle);
    return p->editor_open.load() ? 1 : 0; // lock-free, never blocks
#else
    (void)handle;
    return 0;
#endif
}

// Render an OSType four-char code into a std::string, trimming trailing
// spaces (the right-pad convention) but preserving embedded ones.
static std::string fourcc_to_string(OSType code) {
    char c[4] = {
        static_cast<char>((code >> 24) & 0xFF),
        static_cast<char>((code >> 16) & 0xFF),
        static_cast<char>((code >> 8) & 0xFF),
        static_cast<char>(code & 0xFF),
    };
    int len = 4;
    while (len > 0 && c[len - 1] == ' ') --len;
    return std::string(c, c + (len > 0 ? len : 4));
}

int32_t zau_enumerate_effects(char* buffer, int32_t buffer_len, int32_t* out_len) {
    if (!out_len) return ZAU_INVALID_ARGUMENTS;

    // Enumerate from the OS AudioComponent registry — the SAME registry Logic
    // Pro and every other macOS AU host reads, covering both standard install
    // locations (/Library/Audio/Plug-Ins/Components + ~/Library/...) with no
    // directory walking. We cover two insert-effect types Logic exposes:
    //   - kAudioUnitType_Effect      ('aufx') — standard audio effects
    //   - kAudioUnitType_MusicEffect ('aumf') — effects that also take MIDI
    // Both load + render through the same AudioUnitRender path, so zau_load
    // accepts either type (it rejects only non-effect types).
    const OSType effectTypes[] = { kAudioUnitType_Effect, kAudioUnitType_MusicEffect };

    std::string acc;
    // AudioComponentCopyName + the CFString helpers below hand back
    // autoreleased temporaries. This entry point is called from the .NET
    // control thread, which has no ambient autorelease pool, so without an
    // explicit one those temporaries accumulate for the life of the process
    // and leak on every scan. Drain them here.
    @autoreleasepool {
    for (OSType effectType : effectTypes) {
    AudioComponentDescription query{};
    query.componentType = effectType;

    AudioComponent comp = nullptr;
    while ((comp = AudioComponentFindNext(comp, &query)) != nullptr) {
        AudioComponentDescription desc{};
        if (AudioComponentGetDescription(comp, &desc) != noErr) continue;

        // CopyName returns "Manufacturer: Effect Name" for most AUs.
        std::string full;
        CFStringRef cfName = nullptr;
        if (AudioComponentCopyName(comp, &cfName) == noErr && cfName) {
            CFIndex n = CFStringGetMaximumSizeForEncoding(
                CFStringGetLength(cfName), kCFStringEncodingUTF8) + 1;
            std::vector<char> tmp(static_cast<size_t>(n), 0);
            if (CFStringGetCString(cfName, tmp.data(), n, kCFStringEncodingUTF8))
                full.assign(tmp.data());
            CFRelease(cfName);
        }

        std::string mfrName = full, effName = full;
        auto colon = full.find(':');
        if (colon != std::string::npos) {
            mfrName = full.substr(0, colon);
            effName = full.substr(colon + 1);
            // trim a leading space after the colon
            while (!effName.empty() && effName.front() == ' ') effName.erase(effName.begin());
        }
        if (effName.empty()) effName = fourcc_to_string(desc.componentSubType);

        std::string id = fourcc_to_string(desc.componentType) + ":" +
                         fourcc_to_string(desc.componentSubType) + ":" +
                         fourcc_to_string(desc.componentManufacturer);

        acc += id;
        acc += '\t';
        acc += effName;
        acc += '\t';
        acc += mfrName;
        acc += '\n';
    }
    } // for effectType
    } // @autoreleasepool

    int32_t needed = static_cast<int32_t>(acc.size());
    *out_len = needed;
    if (!buffer || buffer_len <= 0) return ZAU_OK;       // size query
    int32_t toCopy = needed < buffer_len ? needed : buffer_len;
    if (toCopy > 0) std::memcpy(buffer, acc.data(), static_cast<size_t>(toCopy));
    return ZAU_OK;
}

} // extern "C"

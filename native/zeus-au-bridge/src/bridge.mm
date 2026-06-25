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

#include <atomic>
#include <cstring>
#include <string>
#include <vector>

namespace {

std::atomic<int> g_init_count{0};

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

    int32_t channels{1};
    int32_t sample_rate{48000};
    int32_t block_size{256};

    // Input buffer-list reused across render calls. Channel pointers are
    // re-pointed at the caller's planar buffer on every zau_process call;
    // we never copy. Storage is sized at load.
    std::vector<uint8_t> input_abl_storage; // AudioBufferList + N AudioBuffer
    const float* current_input{nullptr};    // set per process() call
    int32_t      current_frames{0};

    // Monotonic host sample-time cursor handed to AudioUnitRender. It MUST
    // advance by `frames` every block: time-based AUs (reverb / delay /
    // chorus / tremolo and any LFO-driven effect) read the render timestamp
    // as their clock. Pinning it to 0 every block — as this bridge originally
    // did — makes those effects reset or stutter at every block boundary
    // (audible as garbled / modulated output), even though a static filter
    // like AULowpass is unaffected. Owned by the realtime thread; the AU
    // serialises process/unload via the handle so no atomics are needed.
    Float64 render_sample_time{0.0};

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

    const int ch = p->channels;
    const UInt32 frames = inNumberFrames;
    for (int c = 0; c < ch && c < static_cast<int>(ioData->mNumberBuffers); ++c) {
        // Non-interleaved: one channel per buffer. Point at the caller's
        // planar slice (channel c starts at index c*frames).
        ioData->mBuffers[c].mNumberChannels = 1;
        ioData->mBuffers[c].mDataByteSize   = frames * sizeof(float);
        ioData->mBuffers[c].mData =
            const_cast<float*>(p->current_input + static_cast<size_t>(c) * p->current_frames);
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

    // Set the non-interleaved float32 stream format on both scopes so the
    // channel-major layout maps with no interleave shuffle.
    AudioStreamBasicDescription asbd = make_asbd(p.channels, static_cast<double>(p.sample_rate));
    st = AudioUnitSetProperty(p.unit, kAudioUnitProperty_StreamFormat,
                              kAudioUnitScope_Input, 0, &asbd, sizeof(asbd));
    if (st != noErr) { teardown(p); return ZAU_ACTIVATE_FAILED; }
    st = AudioUnitSetProperty(p.unit, kAudioUnitProperty_StreamFormat,
                              kAudioUnitScope_Output, 0, &asbd, sizeof(asbd));
    if (st != noErr) { teardown(p); return ZAU_ACTIVATE_FAILED; }

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
    if (st != noErr) { teardown(p); return ZAU_ACTIVATE_FAILED; }

    // Pre-size the output AudioBufferList scratch: one AudioBuffer per
    // channel (non-interleaved). AudioBufferList carries one mBuffers entry
    // inline, so add (channels-1) more.
    size_t ablBytes = sizeof(AudioBufferList) +
                      (p.channels > 0 ? (p.channels - 1) : 0) * sizeof(AudioBuffer);
    p.input_abl_storage.assign(ablBytes, 0);

    return ZAU_OK;
}

} // namespace

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

    // Point the output AudioBufferList at the caller's planar output slices.
    AudioBufferList* abl = p->input_abl();
    abl->mNumberBuffers = static_cast<UInt32>(p->channels);
    for (int c = 0; c < p->channels; ++c) {
        abl->mBuffers[c].mNumberChannels = 1;
        abl->mBuffers[c].mDataByteSize   = static_cast<UInt32>(frames) * sizeof(float);
        abl->mBuffers[c].mData = output + static_cast<size_t>(c) * frames;
    }

    AudioUnitRenderActionFlags flags = 0;
    AudioTimeStamp ts{};
    ts.mFlags       = kAudioTimeStampSampleTimeValid;
    ts.mSampleTime  = p->render_sample_time;

    OSStatus st = AudioUnitRender(p->unit, &flags, &ts, 0,
                                  static_cast<UInt32>(frames), abl);
    p->current_input = nullptr;
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
    teardown(*p);
    delete p;
    return ZAU_OK;
}

int32_t zau_shutdown(void) {
    if (g_init_count.load() > 0) g_init_count.fetch_sub(1);
    return ZAU_OK;
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

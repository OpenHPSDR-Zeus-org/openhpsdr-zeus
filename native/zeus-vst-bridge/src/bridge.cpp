// SPDX-License-Identifier: GPL-2.0-or-later
//
// Openhpsdr-Zeus VST3 host bridge.
//
// Loads VST3 plugins in-process via Steinberg's MIT-licensed vst3sdk
// (vendored under third_party/vst3sdk/) and runs them on the .NET side's
// realtime audio thread via the C ABI in include/zvst.h.
//
// Threading model: zvst_init / load / unload / set_param run on the .NET
// control thread; zvst_process runs on the realtime audio thread. The
// loaded-plugin state struct is owned exclusively by the handle returned
// to .NET; the .NET wrapper guarantees serialised access (no parallel
// process / unload).

#include "zvst.h"

#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/processdata.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/vsttypes.h"
#include "pluginterfaces/vst/ivstmessage.h" // IConnectionPoint
#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/base/funknown.h"
#include "pluginterfaces/base/ftypes.h"
#include "pluginterfaces/base/ibstream.h"
#include "base/source/fobject.h"
#include "public.sdk/source/common/memorystream.h"

#include <atomic>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include <cstring>
#include <cstdio>

// Editor (IPlugView) hosting is Windows-only for now — the plug-in GUI
// is a native window we create + message-pump on a dedicated thread.
#ifdef _WIN32
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  include <windows.h>
#  include <objbase.h> // CoInitializeEx / CoUninitialize
#  include <ole2.h>    // OleInitialize / OleUninitialize (VSTGUI needs OLE)
#endif

namespace vst = Steinberg::Vst;

namespace {

// Process-wide host application. vst3sdk hosting helpers expect the
// host to expose IHostApplication; HostApplication from hostclasses.h
// is the stock implementation.
class GlobalHost {
public:
    static vst::HostApplication& instance() {
        static GlobalHost g;
        return g.app_;
    }
private:
    GlobalHost() = default;
    vst::HostApplication app_;
};

std::atomic<int> g_init_count{0};

// Forward declarations so the editor's component handler can push GUI
// parameter edits into the per-plugin queue defined on LoadedPlugin.
struct LoadedPlugin;
static void zvst_push_param_edit(LoadedPlugin* p, uint32_t param_id, double normalized);

#ifdef _WIN32
// ---------------------------------------------------------------------
// Editor (plug-in GUI) host support — Windows. Both helper objects have
// *inert* reference counting (addRef/release return a constant): their
// lifetime is owned by us (value members of LoadedPlugin that outlive the
// view), not the plug-in.
// ---------------------------------------------------------------------

// Component handler: the plug-in calls performEdit() on the UI thread
// when the operator moves a control in the editor. We forward the
// normalized value into the plug-in's thread-safe edit queue, which the
// realtime process() drains into the audio processor — so GUI knob moves
// actually change the sound (like a standalone VST host).
class ZeusComponentHandler : public vst::IComponentHandler {
public:
    LoadedPlugin* owner{nullptr};

    Steinberg::tresult PLUGIN_API beginEdit(vst::ParamID) SMTG_OVERRIDE { return Steinberg::kResultOk; }
    Steinberg::tresult PLUGIN_API performEdit(vst::ParamID id, vst::ParamValue v) SMTG_OVERRIDE {
        if (owner) zvst_push_param_edit(owner, static_cast<uint32_t>(id), v);
        return Steinberg::kResultOk;
    }
    Steinberg::tresult PLUGIN_API endEdit(vst::ParamID) SMTG_OVERRIDE { return Steinberg::kResultOk; }
    Steinberg::tresult PLUGIN_API restartComponent(Steinberg::int32) SMTG_OVERRIDE { return Steinberg::kResultOk; }

    Steinberg::tresult PLUGIN_API queryInterface(const Steinberg::TUID iid, void** obj) SMTG_OVERRIDE {
        if (Steinberg::FUnknownPrivate::iidEqual(iid, Steinberg::FUnknown::iid) ||
            Steinberg::FUnknownPrivate::iidEqual(iid, vst::IComponentHandler::iid)) {
            *obj = static_cast<vst::IComponentHandler*>(this);
            return Steinberg::kResultOk;
        }
        *obj = nullptr;
        return Steinberg::kNoInterface;
    }
    Steinberg::uint32 PLUGIN_API addRef() SMTG_OVERRIDE { return 1000; }
    Steinberg::uint32 PLUGIN_API release() SMTG_OVERRIDE { return 1000; }
};

// Per-editor plug frame. Lets the plug-in ask the host to resize its
// window (IPlugFrame::resizeView). Stored by value on LoadedPlugin.
class ZeusPlugFrame : public Steinberg::IPlugFrame {
public:
    HWND hwnd{nullptr};

    Steinberg::tresult PLUGIN_API resizeView(Steinberg::IPlugView* view,
                                             Steinberg::ViewRect* newSize) SMTG_OVERRIDE {
        std::fprintf(stderr, "[zvst-editor] resizeView %dx%d\n",
                     newSize ? newSize->getWidth() : -1, newSize ? newSize->getHeight() : -1);
        std::fflush(stderr);
        if (!view || !newSize || !hwnd) return Steinberg::kResultFalse;
        RECT r{0, 0, newSize->getWidth(), newSize->getHeight()};
        WINDOWINFO wi{}; wi.cbSize = sizeof(wi);
        GetWindowInfo(hwnd, &wi);
        AdjustWindowRectEx(&r, wi.dwStyle, FALSE, wi.dwExStyle);
        SetWindowPos(hwnd, nullptr, 0, 0, r.right - r.left, r.bottom - r.top,
                     SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        view->onSize(newSize);
        return Steinberg::kResultTrue;
    }

    Steinberg::tresult PLUGIN_API queryInterface(const Steinberg::TUID iid, void** obj) SMTG_OVERRIDE {
        if (Steinberg::FUnknownPrivate::iidEqual(iid, Steinberg::FUnknown::iid) ||
            Steinberg::FUnknownPrivate::iidEqual(iid, Steinberg::IPlugFrame::iid)) {
            *obj = static_cast<Steinberg::IPlugFrame*>(this);
            return Steinberg::kResultOk;
        }
        *obj = nullptr;
        return Steinberg::kNoInterface;
    }
    Steinberg::uint32 PLUGIN_API addRef() SMTG_OVERRIDE { return 1000; }
    Steinberg::uint32 PLUGIN_API release() SMTG_OVERRIDE { return 1000; }
};
#endif // _WIN32

// Per-handle state. Owns one IComponent + IAudioProcessor, plus the
// scratch ProcessData buffers sized at load time so the realtime path
// doesn't allocate.
struct LoadedPlugin {
    std::shared_ptr<VST3::Hosting::Module> module;
    Steinberg::IPtr<vst::IComponent>       component;
    Steinberg::IPtr<vst::IAudioProcessor>  processor;
    Steinberg::IPtr<vst::IEditController>  controller; // optional

    int32_t channels{1};
    int32_t sample_rate{48000};
    int32_t block_size{256};

    // Pre-sized planar buffers; .NET passes pointers into these via the
    // process callback. We don't own .NET's memory but re-point each
    // call.
    std::vector<float*> in_buffer_ptrs;
    std::vector<float*> out_buffer_ptrs;

    // ProcessData reused across calls. AudioBusBuffers vectors must be
    // stable storage because ProcessData stores raw pointers into them.
    vst::AudioBusBuffers              in_bus{};
    vst::AudioBusBuffers              out_bus{};
    vst::ProcessData                  process_data{};
    vst::ProcessSetup                 process_setup{};
    vst::ParameterChanges             input_changes; // for set_param queueing

    // True when `controller` is a *separate* edit-controller object we
    // created + initialized (vs. a single-component effect where the
    // component IS the controller). Governs whether teardown terminates
    // the controller independently.
    bool controller_is_separate{false};

    // --- GUI parameter edits → audio (thread-safe handoff) ---
    // performEdit() (UI thread) and zvst_set_param() (control thread)
    // append (id, value) here under param_mtx; the realtime process()
    // drains them into the processor via try_lock so it never blocks.
    // Bounded buffer — 256 pending edits between audio blocks (~5 ms) is
    // implausible, so overflow just drops the oldest excess.
    struct ParamEdit { uint32_t id; double value; };
    static constexpr int kParamCap = 256;
    std::mutex param_mtx;
    ParamEdit  param_buf[kParamCap];
    int        param_count{0};

#ifdef _WIN32
    // The plug-in is loaded, edited, and unloaded on ONE persistent UI
    // thread (its own STA + message pump), so the component, controller,
    // and editor all share a single thread affinity — which VSTGUI/JUCE
    // editors require (a cross-thread editor deadlocks in attached()).
    // Audio process() is the sole exception: it's still called directly
    // from the realtime audio thread on the processor (VST3 permits that),
    // never marshaled onto this thread.
    std::string       load_path;                 // captured for the UI thread
    std::thread       ui_thread;
    HANDLE            ready_evt{nullptr};         // signaled when load + coordinator ready
    std::atomic<int>  load_status{ZVST_OTHER};
    std::atomic<bool> ui_exited{false};
    HWND              coordinator_hwnd{nullptr};  // message-only command sink

    // Editor sub-state — touched only on the UI thread.
    std::string                           editor_title;
    std::atomic<bool>                     editor_open_flag{false};
    HWND                                  editor_hwnd{nullptr};
    Steinberg::IPtr<Steinberg::IPlugView> view;
    ZeusPlugFrame                         plug_frame;
    ZeusComponentHandler                  component_handler;
#endif
};

// Producer side of the param queue — called from the UI thread
// (performEdit) and the control thread (zvst_set_param). Clamps to
// [0,1] and drops on overflow.
static void zvst_push_param_edit(LoadedPlugin* p, uint32_t param_id, double normalized) {
    if (normalized < 0.0) normalized = 0.0;
    if (normalized > 1.0) normalized = 1.0;
    std::lock_guard<std::mutex> lk(p->param_mtx);
    if (p->param_count < LoadedPlugin::kParamCap)
        p->param_buf[p->param_count++] = { param_id, normalized };
}

// Look up the first kVstAudioEffectClass in the factory.
Steinberg::IPtr<vst::IComponent>
instantiate_first_audio_effect(const VST3::Hosting::PluginFactory& factory,
                               int32_t* status_out)
{
    auto class_infos = factory.classInfos();
    for (const auto& ci : class_infos) {
        if (ci.category() == kVstAudioEffectClass) {
            auto comp = factory.createInstance<vst::IComponent>(ci.ID());
            if (comp) return comp;
        }
    }
    *status_out = ZVST_NO_AUDIO_EFFECT_CLASS;
    return nullptr;
}

bool wire_buses_and_activate(LoadedPlugin& p, int32_t* status_out) {
    using namespace Steinberg;

    if (p.component->setActive(false) != kResultOk) {
        // not fatal; some plugins return error here pre-init
    }

    if (p.component->setIoMode(vst::kAdvanced) != kResultOk) {
        // optional, ignore
    }

    if (p.component->initialize(&GlobalHost::instance()) != kResultOk) {
        *status_out = ZVST_ACTIVATE_FAILED;
        return false;
    }

    // Query the audio-processor interface from the component. A previous
    // revision ALSO called queryInterface through p.processor.get() here —
    // but p.processor is a default-constructed (null) IPtr, so that wrote
    // the result through a null void** and segfaulted the host on the first
    // real plugin load. Query into a raw pointer, then adopt it.
    vst::IAudioProcessor* raw_proc = nullptr;
    if (p.component->queryInterface(vst::IAudioProcessor::iid,
            reinterpret_cast<void**>(&raw_proc)) != kResultOk || !raw_proc) {
        *status_out = ZVST_NOT_A_VST3;
        return false;
    }
    p.processor = Steinberg::owned(raw_proc);

    // Speaker arrangement — mono or stereo.
    vst::SpeakerArrangement arr = (p.channels == 1)
        ? vst::SpeakerArr::kMono
        : vst::SpeakerArr::kStereo;
    if (p.processor->setBusArrangements(&arr, 1, &arr, 1) != kResultOk) {
        // Some plugins are stereo-only; try stereo as a fallback for
        // mono request.
        if (p.channels == 1) {
            arr = vst::SpeakerArr::kStereo;
            if (p.processor->setBusArrangements(&arr, 1, &arr, 1) != kResultOk) {
                *status_out = ZVST_ACTIVATE_FAILED;
                return false;
            }
        } else {
            *status_out = ZVST_ACTIVATE_FAILED;
            return false;
        }
    }

    p.process_setup.processMode = vst::kRealtime;
    p.process_setup.symbolicSampleSize = vst::kSample32;
    p.process_setup.maxSamplesPerBlock = p.block_size;
    p.process_setup.sampleRate = static_cast<double>(p.sample_rate);
    if (p.processor->setupProcessing(p.process_setup) != kResultOk) {
        *status_out = ZVST_ACTIVATE_FAILED;
        return false;
    }

    // Activate buses
    int32_t in_bus_count  = p.component->getBusCount(vst::kAudio, vst::kInput);
    int32_t out_bus_count = p.component->getBusCount(vst::kAudio, vst::kOutput);
    if (in_bus_count > 0)  p.component->activateBus(vst::kAudio, vst::kInput,  0, true);
    if (out_bus_count > 0) p.component->activateBus(vst::kAudio, vst::kOutput, 0, true);

    if (p.component->setActive(true) != kResultOk) {
        *status_out = ZVST_ACTIVATE_FAILED;
        return false;
    }
    if (p.processor->setProcessing(true) != kResultOk) {
        // Some plugins return kNotImplemented here — treat as soft success.
    }

    // ProcessData scratch — point bus buffers at our per-channel pointer
    // vectors; the actual data pointers are refreshed on every process
    // call (input/output are caller-owned buffers).
    p.in_buffer_ptrs.assign(static_cast<size_t>(p.channels), nullptr);
    p.out_buffer_ptrs.assign(static_cast<size_t>(p.channels), nullptr);
    p.in_bus.numChannels  = p.channels;
    p.out_bus.numChannels = p.channels;
    p.in_bus.channelBuffers32  = p.in_buffer_ptrs.data();
    p.out_bus.channelBuffers32 = p.out_buffer_ptrs.data();
    p.in_bus.silenceFlags = 0;
    p.out_bus.silenceFlags = 0;

    p.process_data.processMode = vst::kRealtime;
    p.process_data.symbolicSampleSize = vst::kSample32;
    p.process_data.numSamples = 0; // set per call
    p.process_data.numInputs  = (in_bus_count  > 0) ? 1 : 0;
    p.process_data.numOutputs = (out_bus_count > 0) ? 1 : 0;
    p.process_data.inputs  = (in_bus_count  > 0) ? &p.in_bus  : nullptr;
    p.process_data.outputs = (out_bus_count > 0) ? &p.out_bus : nullptr;
    p.process_data.inputParameterChanges = &p.input_changes;

    return true;
}

void teardown(LoadedPlugin& p) {
    if (p.processor) {
        p.processor->setProcessing(false);
    }
    // A separately-created edit controller (we called initialize on it
    // when the editor was opened) must be detached + terminated on its
    // own. For single-component effects controller == component, so the
    // component's terminate() below covers it — don't double-terminate.
    if (p.controller && p.controller_is_separate) {
        p.controller->setComponentHandler(nullptr);
        p.controller->terminate();
    }
    p.controller = nullptr;
    if (p.component) {
        p.component->setActive(false);
        p.component->terminate();
    }
    p.processor = nullptr;
    p.component = nullptr;
    p.module = nullptr;
}

// Load the module, instantiate the first audio-effect component, and
// wire + activate it for processing at p's geometry. Platform-agnostic;
// on Windows this runs on the persistent UI thread, elsewhere on the
// caller's thread. Returns ZVST_OK or a failure status.
static int do_load(LoadedPlugin& p, const std::string& path) {
    std::string err;
    p.module = VST3::Hosting::Module::create(path, err);
    if (!p.module) {
        FILE* probe = fopen(path.c_str(), "rb");
        if (probe) { fclose(probe); return ZVST_NOT_A_VST3; }
        return ZVST_FILE_NOT_FOUND;
    }

    auto& factory = p.module->getFactory();
    int status = ZVST_OK;
    p.component = instantiate_first_audio_effect(factory, &status);
    if (!p.component) return status;

    if (!wire_buses_and_activate(p, &status)) {
        teardown(p);
        return status;
    }
    return ZVST_OK;
}

#ifdef _WIN32
// ---------------------------------------------------------------------
// Persistent per-plugin UI thread: loads the plug-in, then services
// editor open/close + unload commands via a message-only coordinator
// window. Everything that touches the component / controller / view
// runs here so the plug-in sees a single thread affinity.
// ---------------------------------------------------------------------

// Diagnostic logging for the editor path — goes to stderr, captured in
// the backend log. Low volume (open/close only), so unconditional.
#define ZED_LOG(...) do { std::fprintf(stderr, "[zvst-editor] " __VA_ARGS__); std::fprintf(stderr, "\n"); std::fflush(stderr); } while (0)

static std::wstring utf8_to_wide(const std::string& s) {
    if (s.empty()) return std::wstring();
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), nullptr, 0);
    if (n <= 0) return std::wstring();
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

static const wchar_t* kZeusEditorWndClass = L"ZeusVstEditorWindow";

// Defined below; closing the editor window tears the view down but keeps
// the plug-in loaded (the UI thread stays alive for a later re-open).
static void editor_close_on_ui(LoadedPlugin& p);

static LRESULT CALLBACK zeus_editor_wndproc(HWND h, UINT msg, WPARAM w, LPARAM l) {
    if (msg == WM_CLOSE) {
        auto* p = reinterpret_cast<LoadedPlugin*>(GetWindowLongPtrW(h, GWLP_USERDATA));
        if (p) editor_close_on_ui(*p); // view->removed() + DestroyWindow
        else   DestroyWindow(h);
        return 0;
    }
    if (msg == WM_ERASEBKGND) return 1; // plug-in paints its own background
    return DefWindowProcW(h, msg, w, l);
}

static void register_editor_wndclass() {
    static std::once_flag once;
    std::call_once(once, [] {
        WNDCLASSEXW wc{};
        wc.cbSize        = sizeof(wc);
        wc.style         = CS_DBLCLKS;
        wc.lpfnWndProc   = zeus_editor_wndproc;
        wc.hInstance     = GetModuleHandleW(nullptr);
        wc.hCursor       = LoadCursorW(nullptr, IDC_ARROW);
        wc.hbrBackground = nullptr;
        wc.lpszClassName = kZeusEditorWndClass;
        RegisterClassExW(&wc);
    });
}

// Wire a freshly-created SEPARATE edit controller to its component:
// open the bidirectional connection-point message channel and push the
// component's current state into the controller. Both are required by
// many plug-ins before they will create their editor view (TDR Nova
// among them — without this, createView() returns null). For a
// single-component effect (controller == component) neither step
// applies, so this is only called on the two-object path.
static void connect_and_sync(LoadedPlugin& p) {
    using namespace Steinberg;
    FUnknownPtr<vst::IConnectionPoint> ccp(p.component);
    FUnknownPtr<vst::IConnectionPoint> ecp(p.controller);
    if (ccp && ecp) {
        ccp->connect(ecp);
        ecp->connect(ccp);
        ZED_LOG("connected component<->controller");
    } else {
        ZED_LOG("connection points missing (component=%d controller=%d)",
                ccp ? 1 : 0, ecp ? 1 : 0);
    }

    MemoryStream stream;
    if (p.component->getState(&stream) == kResultOk) {
        stream.seek(0, IBStream::kIBSeekSet, nullptr);
        tresult sr = p.controller->setComponentState(&stream);
        ZED_LOG("setComponentState res=%d", static_cast<int>(sr));
    } else {
        ZED_LOG("component getState failed");
    }
}

// Lazily create (or adopt) the edit controller. Prefer a dedicated
// controller class (the common 2-object plug-in design); fall back to a
// single-component effect where the component itself is the controller.
static bool ensure_controller(LoadedPlugin& p) {
    using namespace Steinberg;
    if (p.controller) return true;

    TUID cid;
    tresult cidRes = p.component->getControllerClassId(cid);
    ZED_LOG("getControllerClassId res=%d", static_cast<int>(cidRes));
    if (cidRes == kResultOk) {
        auto& factory = p.module->getFactory();
        auto ctrl = factory.createInstance<vst::IEditController>(VST3::UID::fromTUID(cid));
        ZED_LOG("createInstance(controller) -> %p", static_cast<void*>(ctrl.get()));
        if (ctrl) {
            tresult initRes = ctrl->initialize(&GlobalHost::instance());
            ZED_LOG("controller->initialize res=%d", static_cast<int>(initRes));
            if (initRes == kResultOk) {
                p.controller = ctrl;
                p.controller_is_separate = true;
                connect_and_sync(p);
                return true;
            }
        }
    }

    // Single-component effect: the component implements IEditController.
    vst::IEditController* raw = nullptr;
    tresult qiRes = p.component->queryInterface(vst::IEditController::iid,
            reinterpret_cast<void**>(&raw));
    ZED_LOG("component QI IEditController res=%d raw=%p", static_cast<int>(qiRes), static_cast<void*>(raw));
    if (qiRes == kResultOk && raw) {
        p.controller = owned(raw);
        p.controller_is_separate = false;
        return true;
    }
    return false;
}

// Close the editor window (if open) and detach the view. Runs on the UI
// thread — from the window's WM_CLOSE, the CLOSE command, or final
// teardown. Idempotent. The plug-in stays loaded so the editor can be
// reopened.
static void editor_close_on_ui(LoadedPlugin& p) {
    if (!p.editor_open_flag.load() && !p.editor_hwnd && !p.view) return;
    if (p.view) {
        p.view->setFrame(nullptr);
        p.view->removed();
        p.view = nullptr;
    }
    p.plug_frame.hwnd = nullptr;
    if (p.editor_hwnd) {
        HWND h = p.editor_hwnd;
        p.editor_hwnd = nullptr;
        DestroyWindow(h);
    }
    p.editor_open_flag.store(false);
    ZED_LOG("editor closed");
}

// Create the plug-in's editor window + attach its view. Runs on the UI
// thread (same thread that loaded the plug-in — that single affinity is
// what lets VSTGUI/JUCE editors complete attached() without deadlocking).
// Returns 1 on success (window shown), 0 on failure.
static int editor_open_on_ui(LoadedPlugin& p) {
    using namespace Steinberg;
    if (p.editor_open_flag.load()) return 1;
    if (!ensure_controller(p)) { ZED_LOG("ensure_controller FAILED"); return 0; }
    p.component_handler.owner = &p;
    p.controller->setComponentHandler(&p.component_handler);

    IPlugView* rawView = p.controller->createView(vst::ViewType::kEditor);
    ZED_LOG("createView(editor) -> %p", static_cast<void*>(rawView));
    if (!rawView) return 0;
    p.view = owned(rawView);

    if (p.view->isPlatformTypeSupported(kPlatformTypeHWND) != kResultTrue) {
        p.view = nullptr;
        return 0;
    }

    ViewRect rect{};
    p.view->getSize(&rect);
    int w = rect.getWidth()  > 0 ? rect.getWidth()  : 800;
    int h = rect.getHeight() > 0 ? rect.getHeight() : 600;

    register_editor_wndclass();
    DWORD style = WS_CAPTION | WS_SYSMENU | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;
    if (p.view->canResize() == kResultTrue) style |= WS_SIZEBOX | WS_MAXIMIZEBOX;
    DWORD exStyle = WS_EX_APPWINDOW;
    RECT wr{0, 0, w, h};
    AdjustWindowRectEx(&wr, style, FALSE, exStyle);

    std::wstring title = utf8_to_wide(p.editor_title.empty() ? std::string("VST3 Plug-in")
                                                             : p.editor_title);
    HWND hwnd = CreateWindowExW(
        exStyle, kZeusEditorWndClass, title.c_str(), style,
        CW_USEDEFAULT, CW_USEDEFAULT, wr.right - wr.left, wr.bottom - wr.top,
        nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    ZED_LOG("CreateWindowExW %dx%d -> hwnd=%p err=%lu",
            w, h, static_cast<void*>(hwnd), GetLastError());
    if (!hwnd) { p.view->setFrame(nullptr); p.view = nullptr; return 0; }
    SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(&p));

    p.editor_hwnd     = hwnd;
    p.plug_frame.hwnd = hwnd;
    p.view->setFrame(&p.plug_frame);

    tresult att = p.view->attached(hwnd, kPlatformTypeHWND);
    ZED_LOG("attached res=%d", static_cast<int>(att));
    if (att != kResultOk) {
        p.view->setFrame(nullptr);
        DestroyWindow(hwnd);
        p.editor_hwnd = nullptr;
        p.plug_frame.hwnd = nullptr;
        p.view = nullptr;
        return 0;
    }

    // The plug-in may have requested a different size during attached().
    ViewRect cur{};
    if (p.view->getSize(&cur) == kResultOk &&
        cur.getWidth() > 0 && cur.getHeight() > 0 &&
        (cur.getWidth() != w || cur.getHeight() != h)) {
        RECT rr{0, 0, cur.getWidth(), cur.getHeight()};
        AdjustWindowRectEx(&rr, style, FALSE, exStyle);
        SetWindowPos(hwnd, nullptr, 0, 0, rr.right - rr.left, rr.bottom - rr.top,
                     SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    ShowWindow(hwnd, SW_SHOW);
    UpdateWindow(hwnd);
    ZED_LOG("editor shown hwnd=%p", static_cast<void*>(hwnd));
    p.editor_open_flag.store(true);
    return 1;
}

// ---- Coordinator: a message-only window the control thread posts
//      commands to, so they execute on the plug-in's UI thread. ----

enum {
    WM_ZVST_OPEN_EDITOR  = WM_APP + 1,
    WM_ZVST_CLOSE_EDITOR = WM_APP + 2,
    WM_ZVST_UNLOAD       = WM_APP + 3,
};

static const wchar_t* kZvstCoordWndClass = L"ZeusVstCoordinator";

static LRESULT CALLBACK zvst_coord_wndproc(HWND h, UINT msg, WPARAM w, LPARAM l) {
    auto* p = reinterpret_cast<LoadedPlugin*>(GetWindowLongPtrW(h, GWLP_USERDATA));
    switch (msg) {
        case WM_ZVST_OPEN_EDITOR:  return p ? editor_open_on_ui(*p) : 0;
        case WM_ZVST_CLOSE_EDITOR: if (p) editor_close_on_ui(*p); return 0;
        case WM_ZVST_UNLOAD:
            if (p) editor_close_on_ui(*p);
            PostQuitMessage(0); // break the UI thread's message loop
            return 0;
        default: return DefWindowProcW(h, msg, w, l);
    }
}

static void register_coord_wndclass() {
    static std::once_flag once;
    std::call_once(once, [] {
        WNDCLASSEXW wc{};
        wc.cbSize        = sizeof(wc);
        wc.lpfnWndProc   = zvst_coord_wndproc;
        wc.hInstance     = GetModuleHandleW(nullptr);
        wc.lpszClassName = kZvstCoordWndClass;
        RegisterClassExW(&wc);
    });
}

// The persistent per-plugin UI thread: load → serve commands → teardown.
// Owns the component/controller/editor for the plug-in's whole lifetime.
static void ui_thread_main(LoadedPlugin* p) {
    OleInitialize(nullptr); // STA + OLE (VSTGUI registers OLE drag/drop)

    int st = do_load(*p, p->load_path);
    p->load_status.store(st);
    if (st != ZVST_OK) {
        ZED_LOG("load failed status=%d", st);
        SetEvent(p->ready_evt);
        OleUninitialize();
        p->ui_exited.store(true);
        return;
    }

    register_coord_wndclass();
    HWND coord = CreateWindowExW(0, kZvstCoordWndClass, L"", 0,
                                 0, 0, 0, 0, HWND_MESSAGE, nullptr,
                                 GetModuleHandleW(nullptr), nullptr);
    if (coord) SetWindowLongPtrW(coord, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(p));
    p->coordinator_hwnd = coord;
    ZED_LOG("loaded; coordinator=%p", static_cast<void*>(coord));
    SetEvent(p->ready_evt); // load done + coordinator ready for commands

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    editor_close_on_ui(*p);
    if (p->coordinator_hwnd) { DestroyWindow(p->coordinator_hwnd); p->coordinator_hwnd = nullptr; }
    teardown(*p);
    OleUninitialize();
    p->ui_exited.store(true);
}
#endif // _WIN32

} // namespace

extern "C" {

int32_t zvst_init(int32_t abi) {
    if (abi != ZVST_ABI) return ZVST_ABI_MISMATCH;
    // Touch the global host to ensure construction happens deterministically.
    (void)GlobalHost::instance();
    g_init_count.fetch_add(1);
    return ZVST_OK;
}

int32_t zvst_load_vst3(
    const char* path,
    int32_t channels,
    int32_t sample_rate,
    int32_t block_size,
    zvst_handle_t* out_handle)
{
    if (!path || !out_handle) return ZVST_INVALID_ARGUMENTS;
    if (channels < 1 || channels > 2) return ZVST_INVALID_ARGUMENTS;
    if (sample_rate < 44100 || sample_rate > 192000) return ZVST_INVALID_ARGUMENTS;
    if (block_size < 32 || block_size > 4096) return ZVST_INVALID_ARGUMENTS;

    auto p = std::make_unique<LoadedPlugin>();
    p->channels = channels;
    p->sample_rate = sample_rate;
    p->block_size = block_size;

#ifdef _WIN32
    // Load on a persistent UI thread so the component, controller, and
    // editor share one thread affinity (required for VSTGUI/JUCE editors).
    // zvst_process still calls the processor directly on the audio thread.
    p->load_path = path;
    p->ready_evt = CreateEventW(nullptr, TRUE /*manual reset*/, FALSE, nullptr);
    if (!p->ready_evt) return ZVST_OTHER;
    try {
        p->ui_thread = std::thread(ui_thread_main, p.get());
    } catch (...) {
        CloseHandle(p->ready_evt);
        return ZVST_OTHER;
    }
    WaitForSingleObject(p->ready_evt, INFINITE);
    int st = p->load_status.load();
    if (st != ZVST_OK) {
        if (p->ui_thread.joinable()) p->ui_thread.join();
        CloseHandle(p->ready_evt);
        p->ready_evt = nullptr;
        return st; // unique_ptr frees p
    }
    *out_handle = static_cast<void*>(p.release());
    return ZVST_OK;
#else
    int status = do_load(*p, path);
    if (status != ZVST_OK) return status;
    *out_handle = static_cast<void*>(p.release());
    return ZVST_OK;
#endif
}

int32_t zvst_process(
    zvst_handle_t handle,
    const float* input,
    float* output,
    int32_t frames)
{
    if (!handle) return ZVST_INVALID_HANDLE;
    if (!input || !output) return ZVST_INVALID_ARGUMENTS;
    auto* p = static_cast<LoadedPlugin*>(handle);
    if (frames < 1 || frames > p->block_size) return ZVST_INVALID_ARGUMENTS;

    // Point each channel's pointer at the right offset in the caller's
    // planar buffers (channel 0 starts at index 0, channel 1 at index
    // `frames`, etc.).
    for (int c = 0; c < p->channels; c++) {
        p->in_buffer_ptrs[c]  = const_cast<float*>(input  + static_cast<size_t>(c) * frames);
        p->out_buffer_ptrs[c] = output + static_cast<size_t>(c) * frames;
    }
    p->process_data.numSamples = frames;

    // Drain GUI / control-thread parameter edits into the processor's
    // input changes for this block. try_lock so the realtime thread NEVER
    // blocks on a producer (performEdit / set_param); a contended block
    // simply applies the edit one block later (~5 ms — imperceptible).
    if (p->param_mtx.try_lock()) {
        for (int i = 0; i < p->param_count; ++i) {
            int32_t qi = 0;
            auto* q = p->input_changes.addParameterData(
                static_cast<Steinberg::Vst::ParamID>(p->param_buf[i].id), qi);
            if (q) { int32_t pi = 0; q->addPoint(0, p->param_buf[i].value, pi); }
        }
        p->param_count = 0;
        p->param_mtx.unlock();
    }

    if (p->processor->process(p->process_data) != Steinberg::kResultOk) {
        // Soft fail — copy input to output and signal status. The .NET
        // wrapper will downgrade the chain to pass-through.
        std::memcpy(output, input,
            static_cast<size_t>(p->channels) * static_cast<size_t>(frames) * sizeof(float));
        return ZVST_OTHER;
    }

    // Clear any queued parameter changes — they've been applied.
    p->input_changes.clearQueue();

    return ZVST_OK;
}

int32_t zvst_set_param(
    zvst_handle_t handle,
    uint32_t param_id,
    double normalized)
{
    if (!handle) return ZVST_INVALID_HANDLE;
    auto* p = static_cast<LoadedPlugin*>(handle);
    // Route through the same thread-safe queue as GUI edits — the prior
    // direct input_changes write raced with the audio thread's process().
    zvst_push_param_edit(p, param_id, normalized);
    return ZVST_OK;
}

int32_t zvst_unload(zvst_handle_t handle) {
    if (!handle) return ZVST_OK;
    auto* p = static_cast<LoadedPlugin*>(handle);
#ifdef _WIN32
    // Ask the UI thread to close any editor + exit its loop; it runs
    // teardown() itself (component lives on that thread). Timeout so a
    // wedged plug-in can never hang the caller.
    if (p->coordinator_hwnd) {
        DWORD_PTR res = 0;
        SendMessageTimeoutW(p->coordinator_hwnd, WM_ZVST_UNLOAD, 0, 0,
                            SMTO_ABORTIFHUNG, 5000, &res);
    }
    for (int i = 0; i < 500 && !p->ui_exited.load(); ++i) Sleep(10);
    if (p->ui_thread.joinable()) {
        if (p->ui_exited.load()) {
            p->ui_thread.join();
        } else {
            // UI thread wedged (plug-in hung). It still references *p, so
            // freeing p would be use-after-free — detach + leak. Rare,
            // memory-safe, never blocks the server.
            p->ui_thread.detach();
            return ZVST_OK;
        }
    }
    if (p->ready_evt) { CloseHandle(p->ready_evt); p->ready_evt = nullptr; }
    delete p; // teardown() already ran on the UI thread
    return ZVST_OK;
#else
    teardown(*p);
    delete p;
    return ZVST_OK;
#endif
}

int32_t zvst_shutdown(void) {
    if (g_init_count.load() > 0) g_init_count.fetch_sub(1);
    return ZVST_OK;
}

int32_t zvst_editor_open(zvst_handle_t handle, const char* title) {
#ifdef _WIN32
    if (!handle) return ZVST_INVALID_HANDLE;
    auto* p = static_cast<LoadedPlugin*>(handle);
    if (!p->coordinator_hwnd) return ZVST_OTHER; // not loaded on the UI thread
    p->editor_title = title ? title : "";
    // Run the open synchronously ON the UI thread via the coordinator.
    // Timeout so a misbehaving plug-in can never hang the caller; with the
    // load + editor on one thread, attached() should complete promptly.
    DWORD_PTR res = 0;
    LRESULT ok = SendMessageTimeoutW(p->coordinator_hwnd, WM_ZVST_OPEN_EDITOR,
                                     0, 0, SMTO_NORMAL, 20000, &res);
    if (!ok) return ZVST_OTHER;            // timed out / UI thread hung
    return res ? ZVST_OK : ZVST_OTHER;     // res = editor_open_on_ui() result
#else
    (void)handle; (void)title;
    return ZVST_NOT_IMPLEMENTED;
#endif
}

int32_t zvst_editor_close(zvst_handle_t handle) {
#ifdef _WIN32
    if (!handle) return ZVST_OK;
    auto* p = static_cast<LoadedPlugin*>(handle);
    if (p->coordinator_hwnd) {
        DWORD_PTR res = 0;
        SendMessageTimeoutW(p->coordinator_hwnd, WM_ZVST_CLOSE_EDITOR, 0, 0,
                            SMTO_ABORTIFHUNG, 5000, &res);
    }
    return ZVST_OK;
#else
    (void)handle;
    return ZVST_OK;
#endif
}

int32_t zvst_editor_is_open(zvst_handle_t handle) {
#ifdef _WIN32
    if (!handle) return 0;
    auto* p = static_cast<LoadedPlugin*>(handle);
    return p->editor_open_flag.load() ? 1 : 0;
#else
    (void)handle;
    return 0;
#endif
}

} // extern "C"

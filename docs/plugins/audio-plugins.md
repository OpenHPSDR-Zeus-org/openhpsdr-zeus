# Audio plugins

Plugins contribute to the Zeus audio chain by implementing
`IAudioPlugin` (custom C# DSP) **or** by declaring `audio.vst3Path` in
their manifest (in-process VST3 hosting via the native bridge).

Both routes feed into the same `AudioChain` — an 8-slot serial chain
with a master enable flag and per-slot bypass. When the master is
off, the chain is a single `Span.CopyTo` — bit-identical to "no chain
at all".

## Contract

```csharp
public interface IAudioPlugin
{
    string DisplayName { get; }
    AudioPluginRequirements Requirements { get; }   // sample rate, channels, block size

    Task InitializeAudioAsync(IAudioHost host, CancellationToken ct);
    void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx);
    Task ShutdownAudioAsync(CancellationToken ct);
}
```

### `Requirements`

```csharp
public sealed record AudioPluginRequirements(
    int SampleRate,
    int Channels,
    int BlockSize);
```

The host honours these and refuses to load the plugin if they can't
be satisfied by the current TX/RX path. For v1, `SampleRate` is
48 kHz and `BlockSize` is 256 frames; future hardware-dependent rates
will negotiate via `IAudioHost`.

### Realtime contract for `Process()`

`Process()` runs on the audio thread. It MUST NOT:

- allocate (`new`, `string` formatting, `List<T>.Add` past capacity)
- lock (no `Monitor.Enter`, `SemaphoreSlim.Wait`, etc.)
- perform IO (file, network, console)
- call into any code that does the above (e.g. logging, EF Core, JSON
  serialisation)

It MAY:

- read/write the `input`/`output` Spans
- read its own pre-allocated state arrays
- call inline math (no virtual dispatch unless you've cached the
  vtable)

Misbehaviour will glitch the on-air audio. The host catches
exceptions thrown from `Process()` and pass-throughs the block, but
it can't catch deadlocks or slow allocations.

In-place processing is supported (call site guarantees `input` and
`output` don't overlap on the chain entry, but slots downstream see
the same buffer on both sides — your plugin should be tolerant of
`input.CopyTo(output); /* mutate output */`).

### Bypass

When the host bypasses a slot, your `Process()` is NOT called. Don't
do anything special — the chain handles the skip.

## Route 1: bundle a VST3, write no C# audio code

Drop a `.vst3` bundle into your plugin zip and reference it:

```json
"audio": {
  "vst3Path": "vst3/MyEffect.vst3",
  "slot": "tx.post-leveler",
  "channels": 1,
  "sampleRate": 48000
}
```

The host synthesises a `VstHostAudioPlugin` that:

1. Calls `zvst_load_vst3(absPath, channels, sampleRate, 256, &handle)`
   on the native bridge.
2. Maps `Process(input, output, ctx)` to `zvst_process(handle, ...)`
   with channel-major planar layout.
3. Calls `zvst_unload(handle)` on shutdown.

The native bridge (under `native/zeus-vst-bridge/`) handles
`Module::create` → factory walk → `IComponent` activation →
`IAudioProcessor::process`. All in-process; no IPC.

## Route 2: implement `IAudioPlugin` in C#

```csharp
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;

namespace Example;

public sealed class GainPlugin : IZeusPlugin, IAudioPlugin
{
    private float _gain = 1.0f;

    // IZeusPlugin
    public Task InitializeAsync(IPluginContext ctx, CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;

    // IAudioPlugin
    public string DisplayName => "Gain";
    public AudioPluginRequirements Requirements => new(48000, 1, 256);

    public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct)
        => Task.CompletedTask;

    public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
    {
        var g = _gain;
        for (int i = 0; i < input.Length; i++) output[i] = input[i] * g;
    }

    public Task ShutdownAudioAsync(CancellationToken ct) => Task.CompletedTask;
}
```

The plugin loader notices the `IAudioPlugin` implementation and
slots it into the chain at the manifest's declared `audio.slot`.
You'd typically expose a parameter via `IBackendPlugin` so the
operator can tweak `_gain` from a panel.

## Chain mechanics

`AudioChain` is the host-owned orchestrator:

- 8 slots, indexed 0..7.
- Master enable (`MasterEnabled`): when false, the chain is a single
  `input.CopyTo(output)` — no per-slot dispatch, no allocation.
- Per-slot `Bypassed` flag — toggle without unloading the plugin.
- Slot mutation methods (`SetSlot`, `ClearSlot`, `SetSlotBypass`) run
  on the control thread, never inside `Process()`.

Internally the chain ping-pongs between the caller's `output` Span
and a pre-allocated scratch buffer so N stages cost exactly N
`Process()` calls plus at most one final copy back to `output`. No
per-block allocation.

## TX-path wiring

`AudioChain` is dispatched into the live WDSP TX path by
`AudioPluginBridge` (`Zeus.Server.Hosting`), a hosted service that
installs a delegate on `WdspDspEngine` via
`SetTxAudioPluginHandler(...)`. The delegate runs on the realtime TX
thread, post-Leveler and **pre-CFC** — CFC stays last, downstream in
WDSP and unaffected by the chain. The same bridge also drives a
pre-MOX live mic preview (`ProcessLivePreview`) so the per-plugin
IN/OUT/GR meters and the Audio Suite "Audition" feed animate from
live mic input even when nothing is being transmitted.

Both routes feed this chain: `IAudioPlugin` plugins (custom C# DSP)
and VST3-hosting plugins (via `audio.vst3Path`, see Route 1 above).
They surface as drag-to-reorder slots in the **Audio Suite** window.
The operator's master bypass short-circuits the whole chain to a
single `Span.CopyTo` — bit-identical to "no chain at all".

## Native bridge build

The native VST3 bridge is not built by `dotnet build`. It hosts VST3s
via Steinberg's MIT-licensed `vst3sdk`, vendored as a git submodule —
initialise it first, then run CMake once:

```bash
git submodule update --init native/zeus-vst-bridge/third_party/vst3sdk
git -C native/zeus-vst-bridge/third_party/vst3sdk \
    submodule update --init base pluginterfaces public.sdk cmake

cd native/zeus-vst-bridge
cmake -B build -DCMAKE_BUILD_TYPE=Release        # Windows: add -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
```

If the `vst3sdk` submodule is absent, CMake falls back to a
pass-through **stub** (no VST3 loading) so the rest of the tree still
builds. With the submodule present it builds the real host.

Output: `zeus-vst-bridge.dll` (Windows, `build/Release/`),
`libzeus-vst-bridge.so` (Linux), `libzeus-vst-bridge.dylib` (macOS).
Two consumers pick it up:

- The test project's `CopyVstBridgeDylib` target copies it next to the
  test binary so `VstBridgeNativeRealTests` P/Invoke resolution finds
  it.
- For shipping, the per-platform binary is staged into
  `Zeus.Plugins.Host/runtimes/<rid>/native/` (committed to the tree
  like `wdsp.dll` / `miniaudio.dll`, rebuilt on demand by the
  **Build Native Libraries** workflow). `Zeus.Plugins.Host.csproj`
  copies `runtimes/**` to the host output, so `.NET`'s native-library
  resolver finds it next to `OpenhpsdrZeus.exe` at runtime.

## Tests

- `AudioChainTests.cs` — 11 tests: empty chain pass-through, master
  disable, single + composed slots, bypass, gap slots, out-of-range
  guards.
- `VstHostAudioPluginTests.cs` — 5 tests: init happy path, missing
  vst3 file, ABI mismatch propagation, pre-init pass-through, bridge
  failure pass-through. Uses an in-process fake `IVstBridgeNative`
  — no native lib required.
- `VstBridgeNativeRealTests.cs` — 5 tests against the actually-built
  dylib. Skip with a friendly message if the bridge isn't built.

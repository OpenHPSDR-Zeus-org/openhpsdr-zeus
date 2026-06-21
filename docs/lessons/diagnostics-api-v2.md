# Live Diagnostics API v2 — debug anything, test everything

Zeus exposes a unified, self-registering diagnostics platform at
`/api/diagnostics/v2`. The design goal: any subsystem can publish live state
through one seam, and every provider that does so is **automatically** tested
across every architecture CI runs on. Adding a feature's diagnostics is one
interface + one DI line; you get the endpoint, the live push frame, the
self-check harness, and test coverage for free.

## The seam: `IDiagnosticsProvider`

`Zeus.Server.Hosting/Diagnostics/IDiagnosticsProvider.cs`. Implement it, then
register the type as a singleton `IDiagnosticsProvider` in
`ZeusHost.Build` (next to the other provider registrations). That's it.

```csharp
public sealed class MyThingProvider : IDiagnosticsProvider
{
    public string Id => "mything.state";        // stable, unique (URLs/tests)
    public string RouteSegment => "mything";    // unique URL segment
    public string Category => "mything";
    public int SchemaVersion => 1;
    public string Description => "What this reports.";
    public object Snapshot() => _svc.Snapshot(); // cheap, read-only, no DSP work
    public IReadOnlyList<DiagnosticsSelfCheck> SelfChecks => new[]
    {
        new DiagnosticsSelfCheck("alive", "Service is responsive.",
            DiagnosticsSeverity.Warn,
            _ => /* read a snapshot */ new SelfCheckResult(SelfCheckOutcome.Pass, "ok", DateTimeOffset.UtcNow)),
    };
}
```

Rules that keep this safe and fast:
- `Snapshot()` returns `object` so you can return an existing (even anonymous)
  DTO **verbatim** — no rewrite, no wire-format change. Typed DTOs that should
  use fast source-gen JSON go in `DiagnosticsJsonContext`; everything else
  falls through to reflection. Both are camelCase + string-enum identical.
- Snapshots and self-checks must be **read-only** and free of realtime/DSP work
  — they read cached state only. Self-checks run on a background timer in
  `DiagnosticsSelfCheckCache`, never on the request or DSP thread, and each
  probe is try/caught (a throwing probe becomes a `fail`, never a 500).
- **PureSignal is read-only here, always.** Diagnostics may surface PS state but
  must never expose arm/disarm or mutate `PsEnabled`. See the hard rule in
  `CLAUDE.md`.

## The surface

- `GET /api/diagnostics/v2` — provider index (metadata only).
- `GET /api/diagnostics/v2/{routeSegment}` — one provider snapshot.
- `GET /api/diagnostics/v2/{routeSegment}/selfcheck` — cached self-check report.
- `POST /api/diagnostics/v2/{routeSegment}/selfcheck` — force a fresh run.
- `GET /api/diagnostics/v2/health` — aggregate worst-of health.

Every `GET .../{routeSegment}` response is wrapped in a uniform self-describing
envelope so all providers look the same on the wire regardless of what the
underlying snapshot returns:

```json
{ "schemaVersion": 1, "id": "dsp.live", "routeSegment": "dsp-live",
  "category": "dsp", "description": "...", "providerSchemaVersion": 1,
  "generatedUtc": "…", "snapshot": { /* the raw provider payload */ } }
```

Legacy diagnostics routes (`/api/dsp/live-diagnostics`, `/api/radio/diagnostics`,
…) are untouched and return the bare payload, so they stay byte-identical for
existing clients. The bare payload equals the v2 envelope's `snapshot`.

## Live push (MsgType 0x36)

`DiagnosticsFramePublisher` (a `BackgroundService`) broadcasts the aggregate
`DiagnosticsHealthDto` over the StreamingHub at ~1 Hz, only when clients are
connected, source-gen serialised. The latest frame is cached and pushed on WS
attach (mirrors SpotList / ChatEvent). Frame layout: `[0x36][UTF-8 JSON]`.
Unknown frame types are ignored by clients, so it is backward-compatible.

## The three test layers (why green ⇒ works)

1. **Runtime self-checks** — each provider declares probes; the cache runs them
   and the health endpoint/push surface the worst-of. This is live, in-product
   debugging.
2. **Conformance harness** (`tests/Zeus.Server.Tests/DiagnosticsConformanceTests.cs`)
   — enumerates every registered provider from DI and asserts the contract
   (200, `schemaVersion`, camelCase, valid self-check outcomes, unique
   ids/routes, resolver-chain preserves string enums, v2 == legacy). A new
   provider is covered the moment it's registered — **no new test file**.
3. **Source-generated per-provider tests** (`Zeus.Diagnostics.TestGen`) — a
   Roslyn incremental generator emits one named xUnit class per provider so each
   shows up individually in the runner and devs can extend it via the `partial`.

## Architecture coverage

`system.platform` reports OS / architecture / RID / .NET version and the WDSP
native load state per platform — the first thing to check for an
architecture-specific bug. CI runs the suite on linux-x64, windows-x64,
macos-arm64 (hard-gated) and linux-arm64, windows-arm64, macos-x64
(experimental until the runners are confirmed green) with a per-arch WDSP
native-load probe — so a green matrix means the diagnostics contract holds on
every architecture, not just the dev's machine.

## Adding coverage

The provider inventory (TX/RX/protocol/DSP/streaming/plugins/persistence/
integration/PureSignal-read-only) is large; the shipped set is representative,
not exhaustive. Each remaining subsystem is a small, independent PR: wrap its
snapshot, register it, done — the harness tests it automatically.

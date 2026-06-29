# RCA: TX VST editor 409s — broken upstream engine + a silent crash-loop

## Symptom

A new user, in VST processing mode, opens a TX Audio Suite VST editor and gets:

> The VST engine is installed but isn't routing yet. Give it a moment, then reopen the editor.

…with repeated `409 Conflict` on `POST /api/tx-audio-suite/plugins/{id}/editor`. "Giving it a
moment" never helps — the engine never starts routing.

## Root cause (two compounding faults)

1. **The downloadable engine was incompatible.** The in-app installer fetched
   KlayaR/VSTHost **"latest" (v1.4.0, May 2026)**. That build's `VSTHostEngine.exe`
   exits before completing the Zeus bridge handshake, so the supervisor counts it
   as a crash, hits the crash-loop cap (`crashed 7× within 60s`), cools down, and
   leaves TX in clean passthrough — `engineActive` stays `false` forever. The only
   bridge-compatible engine was a **newer build than any public release**, present
   only on developer machines. So *every* fresh "Download VST Engine" produced a
   crash-looping engine.

2. **The failure was invisible.** `VstEngineController` tracked `State`, `LastFault`,
   the exit code, and `RestartCount`, but none of it was surfaced. The editor 409
   said only "give it a moment" — optimistic wording for what was actually a
   permanent crash-loop. Diagnosis required reading the in-memory diagnostic log
   (`POST /api/diagnostics/report`) by hand.

The standalone reproduction (`VSTHostEngine.exe --zeus-bridge --shm probe …` exits 0,
no output) was a red herring: the `probe` shared-memory segment doesn't exist, so the
engine bailed cleanly *before* the path that crashes under a real Zeus-created SHM.

## Fix — self-healing distribution

The engine is now sourced from a **Zeus-controlled, SHA-256-pinned manifest** on the
download domain instead of a floating upstream "latest":

- `VstEngineInstaller` fetches `https://downloads.openhpsdrzeus.com/vst-engine/latest.json`,
  downloads the named asset, **verifies its SHA-256**, and stages it. The legacy
  GitHub-release path remains only behind the `ZEUS_VST_ENGINE_RELEASE_URL` dev override.
- **Auto-repair:** when the engine crash-loops (`AudioProcessingModeService.OnEngineFaulted`),
  Zeus re-downloads the verified engine **once** and the supervisor relaunches onto it —
  no operator action. A persistently-bad manifest can't loop (one repair per explicit
  mode set).
- **Status surface:** `GET /api/tx-audio-suite/processing-mode` now returns
  `engineState` / `engineCrashLooping` / `engineRestartCount` / `engineLastFault`. The
  editor hook waits out a cold start and retries, but on a true crash-loop it stops and
  the panel shows a **Repair Engine** button (`POST …/vst-engine/repair`).

So a broken/stale install (like the one that triggered this RCA) now repairs itself on
the next launch once a good engine is published.

## Operator action: publish a known-good engine

This is the one step CI can't fully automate (the engine isn't built in this repo),
but it no longer has to be done by hand. Run the **Publish VST engine** workflow
(`.github/workflows/publish-vst-engine.yml`, `workflow_dispatch`) with:

- `engine_url` — a direct download URL for the bridge-compatible `VSTHostEngine.exe`
- `version` — e.g. `2026.06.14`
- `expected_sha256` — the known-good hash (optional but recommended; the run is
  rejected on mismatch)

The workflow generates the manifest with `tools/vst-engine-manifest.mjs`, uploads the
engine + `latest.json` to the R2 bucket with `--remote`, and verifies the public URLs
end-to-end before passing. (The `--remote` flag is load-bearing: without it wrangler
writes to a local simulator and still reports success — publishing nothing. That, plus
the un-published manifest, is the failure mode this RCA is about.)

Equivalent manual fallback, hosting the verified engine + manifest yourself:

1. Generate the manifest from a known-good `VSTHostEngine.exe`:
   ```
   node tools/vst-engine-manifest.mjs <VSTHostEngine.exe> <version>
   ```
2. Upload, to `https://downloads.openhpsdrzeus.com/vst-engine/` (wrangler needs
   `--remote`, or use the dashboard):
   - the engine as the versioned name it prints (e.g. `VSTHostEngine-2026.06.14.exe`)
   - the manifest as `latest.json`

The current known-good engine (build dated 2026-06-14):

```json
{
  "version": "2026.06.14",
  "assets": [
    {
      "filename": "VSTHostEngine-2026.06.14.exe",
      "url": "https://downloads.openhpsdrzeus.com/vst-engine/VSTHostEngine-2026.06.14.exe",
      "size": 6712832,
      "sha256": "f8bcc1a544cd4c49a2eb89cbf51d613e87139cdf95d459cc52daf2b3d46537e6",
      "platform": "windows",
      "arch": "x64"
    }
  ]
}
```

Once that's live, existing broken installs self-repair on next VST activation, and new
installs get the verified engine on first download.

## Lesson

Don't pin a safety-adjacent native dependency to a floating upstream "latest" — pin a
hash you control. And when a background process can silently fail into passthrough,
surface `State`/`LastFault`/exit-code to the operator; "give it a moment" must not be the
only thing a permanent failure ever says.

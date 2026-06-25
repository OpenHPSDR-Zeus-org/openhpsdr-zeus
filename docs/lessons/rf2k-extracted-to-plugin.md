# RF2K-S amplifier moved out of Zeus core into a plugin

**Date:** 2026-05-17
**Branch:** `feature/extract-rf2k-plugin`
**Removal commit:** `acd1a97` — *refactor(rf2k): remove RF2K-S amplifier integration from Zeus core*
**Plugin id (replacement):** `com.openhpsdr.zeus.plugins.rf2k`
**Plugin source:** [`openhpsdr-zeus-plugins`](https://github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins) repo, `samples/Rf2k/` (branch `feature/rf2k-plugin` at the time of writing).

## What changed

The RF-Kit RF2K-S amplifier integration was the first board-external
device shipped in Zeus core. As of `acd1a97` it no longer is. Every
piece of the integration now lives in a Zeus plugin:

Deleted from core (see `acd1a97` for the full diff):

- `Zeus.Server.Hosting/Rf2kService.cs`
- `Zeus.Server.Hosting/Rf2kVncClient.cs`
- `Zeus.Server.Hosting/Rf2kSettingsStore.cs`
- `Zeus.Contracts/Rf2kDtos.cs`
- `zeus-web/src/api/rf2k.ts`
- `zeus-web/src/state/rf2k-store.ts`
- `zeus-web/src/layout/panels/Rf2kPanel.tsx`

Removed wire-up:

- The four `Rf2k*` DI registrations in `Zeus.Server.Hosting/ZeusHost.cs`.
- The `/api/rf2k/*` route block in `Zeus.Server.Hosting/ZeusEndpoints.cs`.
- The `Rf2kPanel` import and `rf2kAmp` entry in `zeus-web/src/layout/panels.ts`.

`dotnet build Zeus.slnx` and `npm run build` are both clean on the
extraction branch.

## What an operator sees

After updating Zeus to a build past `acd1a97`, the RF2K panel and
the **Add Panel → RF2K Amplifier** option disappear. To get the
amplifier back, install the plugin:

```
Settings → Plugins → Browse  → install "RF2K-S Amplifier"
```

or sideload the zip from the plugin's GitHub release. The panel will
re-appear under **Add Panel → workspace.amplifier** once the plugin
activates.

## Wire format — unchanged

Nothing on the wire changed. The plugin talks to the amp the same
way Zeus core used to:

- **REST JSON on TCP :8080** — `info` / `data` / `power` / `tuner` /
  `operate-mode` / `operational-interface` / `antennas`.
- **VNC (RFB) on TCP :5900** — single PointerEvent click injection for
  the Tune and Bypass front-panel buttons (the REST API does not
  expose these; see the preamble in the old `Rf2kVncClient.cs` for the
  full rationale, preserved verbatim in the plugin).

Firewall rules that worked for in-tree Zeus continue to work for the
plugin. So do the amplifier's own network settings — the operator
does **not** need to reconfigure the amp.

## Persisted settings — may need re-entry

The old `Rf2kSettingsStore` wrote to a LiteDB collection inside
`zeus-prefs.db` that the plugin runtime does not see. The plugin's
settings now live in the **plugin-scoped `IPluginSettings`** the host
hands to it at activation (`IPluginContext.Settings`), which is a
separate per-plugin collection.

Practical consequence: the host IP, VNC password, polling interval,
and similar prefs that an operator entered on a pre-`acd1a97` Zeus
build will not migrate automatically. After installing the plugin,
re-enter them in the new panel's settings tab. Wire-format
compatibility means the amp itself accepts them unchanged.

If a migration script is wanted later, the in-tree collection name
was `rf2k_settings`; the documents are JSON-encoded
`Zeus.Contracts.Rf2kSettings` records and can be lifted into the new
plugin store with a small one-off tool.

## Why

This is the first concrete payoff of the plugin-system rebuild
landed earlier on `develop`: every external-device integration —
amplifiers, tuners, rotators, antenna switches — ships as a plugin
going forward, never as a core dependency. The benefits:

- Operators add and remove device support without waiting for a Zeus
  release.
- Bugs and protocol changes for a specific device do not block other
  Zeus work or cause unrelated regressions.
- The Zeus core surface shrinks back to what it should be: radio,
  DSP, panadapter, audio. Vendor-specific REST clients and VNC
  injection are not core concerns.

RF2K-S is the first migration; PGXL (Power Genius XL) and TGXL
(Tuner Genius XL) ship as new plugins in the same coordinated
release. Future device integrations should start as plugins from day
one — adding them to core and then extracting them is wasted motion.

## See also

- `samples/Rf2k/README.md` in the plugin repo — operator-facing
  install + known-limits doc.
- `samples/Amplifier/` in the plugin repo — canonical reference for
  the plugin contract.
- `Zeus.Plugins.Contracts/` in this repo — the contract every plugin
  is built against.

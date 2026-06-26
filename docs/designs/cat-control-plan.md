# CAT control (Kenwood TS-2000 over TCP) — implementation plan

Status: **in progress (draft PR build, loop)**. Goal: CAT control to parity with Thetis / MI0BOT
Thetis for Hermes Lite, mirroring the existing TCI server. New **Network** settings tab absorbs the
TCI tab and adds CAT beside it. No PureSignal changes; no new deps; cross-platform; TCP only.

## Architecture (mirrors `Zeus.Server.Hosting/Tci/` seam-for-seam)

One deliberate divergence: TCI rides Kestrel (WebSocket-over-HTTP via the ZeusHost `UseWhen`
port-branch). CAT is a raw ASCII line protocol Kestrel can't serve, so **`CatServer` owns its own
`TcpListener` accept loop** inside `StartAsync`. Everything else mirrors TCI.

New backend files (each models the named TCI twin):
- `Zeus.Server.Hosting/Cat/CatOptions.cs` ← TciOptions (Enabled=false, BindAddress=127.0.0.1, Port=19090, RateLimitMs, LimitPowerLevels, SendInitialStateOnConnect).
- `Zeus.Server.Hosting/Cat/CatProtocol.cs` ← TciProtocol (static parse/format; Kenwood mode map; `FormatFreq` 11-digit; `BuildIf` 35-char TS-2000 IF frame).
- `Zeus.Server.Hosting/Cat/CatSession.cs` ← TciSession (per-client SendLoop+ReceiveLoop; accumulate bytes until `;` with a capped buffer; dispatch switch; `AutoInfo` flag gates async pushes).
- `Zeus.Server.Hosting/Cat/CatServer.cs` ← TciServer (IHostedService; owns TcpListener accept loop; subscribes RadioService.StateChanged/MoxChanged + RxMeter; ConcurrentDictionary clients; broadcast only to AI-enabled sessions).
- `Zeus.Server.Hosting/CatConfigStore.cs` ← TciConfigStore (LiteDB single-row `cat_config`, Connection=shared, Directory guard — same pattern, no new #682 exposure).
- `Zeus.Server.Hosting/CatManagementService.cs` ← TciManagementService (GetStatus/SetConfig/TestPort; RequiresRestart; self-port-probe skip).
- `Zeus.Contracts/CatDtos.cs` ← TciDtos (CatRuntimeConfig/CatStatus/CatTestRequest/CatTestResult — additive Contracts, red-light).
- `Zeus.Contracts/MoxSource.cs` EDIT: append `Cat = 5` (append-only; red-light, sign-off).

Wiring: `ZeusHost.cs` DI + persisted `PostConfigure<CatOptions>` (mirror TCI block; **no** Kestrel/port-branch entry — CAT owns its socket). `ZeusEndpoints.cs`: `/api/cat/status|config|test`. `appsettings.json`: disabled-by-default `Cat` section.

## Verified Zeus seams (CAT reuses, no new radio-path code)
`_radio.Snapshot()`, `_radio.SetVfo(hz, fromExternal:true)`, `_radio.SetMode(mode)`, `_radio.SetFilter(lo,hi)`,
`_radio.SetDrive(pct)`, `_tx.TrySetMox(on, MoxSource.Cat, out _)`, `_tx.TrySetTun(on, out _)`, `_tx.IsMoxOn/IsTunOn`.
Echo post-call truth after MOX (MSHV lesson).

## Command coverage (Kenwood TS-2000 subset)
**Tier 1 (now):** `ID;`→`ID019;` · `PS;`→`PS1;` · `AI/AI<n>;` (async gate) · `FA/FA<11>;` (VFO A) · `FB/FB<11>;` (VFO B) ·
`MD/MD<n>;` (mode map 1=LSB,2=USB,3=CWL,4=FM,5=AM,6=DIGL,7=CWU,9=DIGU) · `IF;` (35-char status) · `TX/TX<n>;`→MOX on (MoxSource.Cat) ·
`RX;`→MOX off · `FR/FT;` (split) · `SM/SM<n>;` (S-meter 0000-0030 from cached RxMeter dBm) · `PC/PC<3>;` (drive, LimitPowerLevels clamp).
**Tier 2 (next PR):** RT/XT/RU/RD/RC (RIT/XIT), AG (AF gain), GT (AGC), FW (filter), KS/KY (CW keyer), SQ/NB/NR.
**Tier 3 (defer):** ZZ* extended set, memory MC/MR/MW, antenna AN, scan SC, band BD/BU.

## Transport
Raw `TcpListener`+`NetworkStream`, ASCII, `;`-terminated; handle split-across-segments AND batched commands;
cap accumulator (~256 B). Default **port 19090** (operator-configurable; no universal TCP-CAT standard), Enabled=false,
bind 127.0.0.1 (localhost = security boundary, no auth — same as TCI; UI warns on non-loopback). AI async updates push
FA/MD/IF to AI-enabled sessions only, VFO rate-limited (reuse TciRateLimiter). **Never** send before a client command;
**never** auto-key on connect. Port/bind change = RequiresRestart (mirror TCI; hot-rebind a documented follow-up).

## UI — Network tab
`SettingsMenu.tsx`: `SettingsTabId` `'tci'`→`'network'`; TABS `{id:'network',label:'NETWORK'}`; render `<NetworkSettingsPanel/>`;
**grep & fix every stale `'tci'` tab ref** (initialTab/deep-links/persisted last-tab → else falls back to PA).
`NetworkSettingsPanel.tsx`: stacks the unchanged `<TciSettingsPanel/>` + new `<CatSettingsPanel/>` as two titled sub-sections (token styling only).
`CatSettingsPanel.tsx` (clone TciSettingsPanel), `api/cat.ts` (clone api/tci.ts), `state/cat-store.ts` (clone tci-store.ts; no localStorage seed / no auto-POST on load).

## Safety (baked in)
PureSignal: zero touches; CAT keys only via `TrySetMox(.., MoxSource.Cat)`. No auto-key/auto-arm on connect (test asserts no MOX after connect+AI).
`MoxSource.Cat=5` → CAT-keyed MOX only releasable by CAT (UI master; trips bypass). No new deps (raw sockets + hand-written parser).
Cross-platform (TcpListener/ASCII/Stopwatch only). Default loopback + UI warn on 0.0.0.0.

## Task iterations
1. **Backend core:** CatOptions, CatDtos, MoxSource.Cat, CatConfigStore, CatProtocol (+ build).
2. **Server+session:** CatServer (TcpListener accept loop + subscriptions) + CatSession (framing + dispatch + AI) (+ build).
3. **Tier-1 commands** wired to verified seams; MOX echo.
4. **Async updates** (StateChanged/MoxChanged/RxMeter → AI sessions, rate-limited).
5. **Management + endpoints + ZeusHost DI + appsettings.**
6. **Frontend** Network tab + CAT panel/store/api + stale-tci-ref fixes (+ npm build).
7. **Tests** (CatProtocol IF/mode round-trip; CatSession dispatch/AI/MOX-ownership/no-auto-key; CatManagementService persist/restart/test via IsolatedPrefsFactory).
8. **Adversarial audit swarm + gates + parity doc + DRAFT PR** (flag Contracts/architecture for sign-off).

## Top risks to verify (skeptical audit)
1. `IF;` 35-char field offsets/widths must match Hamlib `kenwood.c` byte-for-byte or rig-detect silently fails.
2. `MoxSource.Cat=5` append-safety where serialized (MoxStateFrame/persisted) + no exhaustive-switch-without-default.
3. Owned TcpListener: graceful StopAsync, half-open detection, partial-read reassembly, bounded accumulator.
4. AI flood rate-limit (reuse TciRateLimiter); poll clients use lock-free Snapshot + cached meter.
5. Per-source (not per-session) MOX ownership edge — mirror TCI, document.
6. Network-tab rename breaking deep-links/persisted tab — grep all `'tci'`.
7. No-auth LAN TX exposure — keep loopback default, warn on 0.0.0.0.

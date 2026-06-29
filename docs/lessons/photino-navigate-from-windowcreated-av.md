# Never navigate the Photino webview from `WindowCreated`

## Symptom

Desktop launch (`OpenhpsdrZeus --desktop`) flashes and dies with a raw Windows
dialog:

> **OpenhpsdrZeus.exe - System Error**
> Exception Processing Message 0xc0000005 - Unexpected parameters

`%LOCALAPPDATA%\Zeus\zeus-startup.log` shows a **completely clean** startup —
WebView2 OK, native libs loadable, prefs usable — reaching
`[phase] desktop: window created, entering message loop` and the geometry
`[geometry] placed at ...` line, then nothing. No `[error]`, no
`AppDomain.UnhandledException`.

The Windows Application event log (provider `.NET Runtime`, Event ID **1026**)
holds the real stack:

```
Description: The process was terminated due to an unhandled exception.
Stack:
   at Photino.NET.PhotinoWindow.Photino_NavigateToUrl(IntPtr, System.String)
   at Photino.NET.PhotinoWindow.Load(System.Uri)
   at Program...<RunDesktop>b__2            ← the RegisterWindowCreatedHandler body
   at Photino.NET.PhotinoWindow.OnWindowCreated()
   at Photino.NET.PhotinoWindow.WaitForClose()
```

## Root cause

`0xc0000005` is a native **access violation**, not a managed exception. It is
also a *corrupted-state exception*: by default .NET will not deliver it to a
managed `try/catch`, `AppDomain.UnhandledException`, or
`TaskScheduler.UnobservedTaskException` — which is why `StartupDiagnostics`'
last-resort handlers never fire and the OS shows its own hard-error dialog
instead of our `ReportStartupFatal` MessageBox.

The fault is `Photino_NavigateToUrl` dereferencing a WebView2 control that does
not exist yet. Photino's `WindowCreated` callback runs **during native window
construction**, *before* `CoreWebView2` is initialised. Calling
`window.Load(url)` from there invokes `Photino_NavigateToUrl` re-entrantly on a
null/garbage control pointer → AV.

It is deterministic, not a corrupt profile: clearing / recreating the Photino
WebView2 user-data folder (`%LOCALAPPDATA%\Photino\EBWebView`) reproduces the
crash identically.

## Fix

Navigate the SPA only once WebView2 is **provably live** — never from
`WindowCreated`. The robust pattern:

1. Load a dark placeholder string as the window's `StartString`. Photino
   navigates this itself once the control is ready (this is *not* re-entrant and
   does not crash).
2. The placeholder posts `zeus.placeholderReady` via
   `window.external.sendMessage(...)` as soon as it loads.
3. The host's `RegisterWebMessageReceivedHandler` navigates to the SPA on that
   message. The webview is alive there (it just ran JS), so `Load` is safe — and
   it still happens *after* the `WindowCreated` resize, preserving the
   no-panel-reflow guarantee from #930.

A pure-JS `location.replace(startUrl)` fallback in the placeholder covers the
(desktop-impossible) case where the host bridge is absent.

See `OpenhpsdrZeus/Program.cs` → `RunDesktop` (the `placeholderHtml` template and
the `zeus.placeholderReady` branch in the WebMessageReceived handler).

## Rules of thumb

- **Photino 4.x exposes no "WebView2 ready" event.** `WindowCreating` and
  `WindowCreated` both fire too early to navigate. The earliest safe signal is a
  web message (or `LocationChanged`) from already-loaded content.
- An `0xc0000005` with a clean `zeus-startup.log` is a native crash **after** the
  last phase marker. Current builds snapshot recent Windows Application crash
  events (`.NET Runtime` 1026 and `Application Error` 1000) into
  `%LOCALAPPDATA%\Zeus\zeus-startup.log` and write best-effort minidumps under
  `%LOCALAPPDATA%\Zeus\crash-dumps\`. Ask the user for both when the app can no
  longer reopen.
- To reproduce/verify headlessly, launch `--desktop`, then check that the process
  survives and that **no new Event 1026** appears (a crash leaves the process hung
  on the modal error dialog, so "still running" alone is not proof — check the
  event log).

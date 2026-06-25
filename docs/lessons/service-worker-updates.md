# Service Worker Update Handling

## Problem

After installing v0.2.0 on Windows, the UI showed the outdated v0.1.0 frontend on first run. Only after restarting Chrome did the updated frontend appear. This was caused by the service worker aggressively caching the old frontend assets.

## Root Cause

The VitePWA plugin was configured with `registerType: 'autoUpdate'`, which theoretically should automatically update the service worker and reload the page. However, this mode has known reliability issues in practice:

1. **Timing issues**: When the app is already open during deployment, the new service worker installs but may not activate until all tabs are closed
2. **Browser caching**: Browsers aggressively cache service workers themselves, leading to stale registrations
3. **No user feedback**: Even when updates are detected, there's no visible indication to the user

## Solution

We switched from `autoUpdate` to `prompt` mode with manual handling via `workbox-window`:

1. **Changed vite.config.ts**:
   - Set `registerType: 'prompt'` instead of `'autoUpdate'`
   - Set `injectRegister: null` to handle registration manually

2. **Created service worker registration handler** (`src/service-worker/registerSW.ts`):
   - Uses `workbox-window` for explicit control
   - Listens for the `waiting` event when a new SW is ready
   - Provides a function to send `SKIP_WAITING` message to activate updates
   - Polls for updates every 60 seconds when page is visible

3. **Created update notification UI** (`src/service-worker/UpdatePrompt.tsx`):
   - Shows a prominent banner at the top of the screen
   - Uses Zeus design system colors (gradient accent blue background)
   - Provides "RELOAD NOW" button for immediate update
   - Automatically reloads the page after update activation

4. **Integrated into App.tsx**:
   - Registers service worker on mount
   - Shows UpdatePrompt when update is available
   - Positions prompt with z-index 10000 to appear above all UI

## How It Works

### Update Detection Flow

1. User has Zeus v0.1.0 open in browser
2. Operator installs Zeus v0.2.0 on the server
3. Service worker polls for updates (every 60 seconds) or detects on page visibility change
4. New service worker installs in the background but waits
5. `waiting` event fires, triggering `onUpdateAvailable` callback
6. UpdatePrompt appears with gradient blue banner
7. User clicks "RELOAD NOW"
8. App sends `SKIP_WAITING` message to waiting service worker
9. Service worker activates and takes control
10. `controlling` event fires, triggering page reload
11. Page reloads with v0.2.0 frontend

### Key Design Decisions

**Prompt over AutoUpdate**: While `autoUpdate` is simpler, it's unreliable. The `prompt` mode with explicit user confirmation is more robust and provides better UX.

**Visible Banner**: The update prompt is deliberately prominent:
- Fixed position at top center of viewport
- Gradient blue background matching Zeus accent colors
- Slides in from top with animation
- z-index 10000 to appear above all content
- Cannot be dismissed (forces user acknowledgment)

**Automatic Reload**: After the user clicks "RELOAD NOW", the page automatically reloads once the service worker activates. No manual refresh needed.

**Polling Interval**: 60-second polls strike a balance between update responsiveness and server load. Updates are typically detected within 1 minute of deployment.

## Testing

To test the update mechanism:

1. Build and run v0.1.0 (or any version)
2. Open Zeus in browser
3. Build and deploy v0.2.0 (or any different version)
4. Wait up to 60 seconds or trigger visibility change (switch tabs and back)
5. Update banner should appear at top of screen
6. Click "RELOAD NOW"
7. Page should reload with new version

**Note**: During development (Vite dev server), service workers are disabled. Testing must be done with production builds.

## Cache headers are a prerequisite (the "Ctrl+Shift+R fixes it" bug)

The prompt flow above only works if the browser actually revalidates `sw.js` and
`index.html` on each load. ASP.NET's `UseStaticFiles` sets `ETag`/`Last-Modified`
but **no `Cache-Control`**, so the browser applies *heuristic* caching and holds
both files without revalidating. Symptoms:

- New visitors load a **stale `index.html`** referencing old hashed chunks → new
  features are missing until a hard reload.
- `reg.update()` fetches a **cached `sw.js`**, never detects the new worker, so the
  "RELOAD NOW" prompt never appears. `Ctrl+Shift+R` "fixes" it only because a hard
  reload bypasses the HTTP cache.

Fix lives in `Zeus.Server.Hosting/ZeusHost.cs` (`UseStaticFiles.OnPrepareResponse`):
content-hashed `/assets/*` get `public, max-age=31536000, immutable`; everything
else (`index.html`, `sw.js`, manifest, icons, the ONNX model) gets `no-cache` so it
revalidates every load (cheap 304s via ETag). The client also checks for updates on
tab focus/visibility (`registerSW.ts`), not just the 60s poll, so a new deploy is
picked up "when they open" the app.

We deliberately did **not** switch to forced auto-reload: Zeus is a live radio
control surface and reloading mid-TX/tune would be unsafe. The operator-confirmed
"RELOAD NOW" prompt stays.

## References

- [Vite PWA Plugin - Prompt for Update](https://vite-pwa-org.netlify.app/guide/prompt-for-update.html)
- [Workbox Window Module](https://developer.chrome.com/docs/workbox/modules/workbox-window/)
- [Service Worker Lifecycle](https://web.dev/service-worker-lifecycle/)

## Related Files

- `zeus-web/vite.config.ts` - VitePWA configuration
- `zeus-web/src/service-worker/registerSW.ts` - Service worker registration
- `zeus-web/src/service-worker/UpdatePrompt.tsx` - Update notification UI
- `zeus-web/src/App.tsx` - Integration point

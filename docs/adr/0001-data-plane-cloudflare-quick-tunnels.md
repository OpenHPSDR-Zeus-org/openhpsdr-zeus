# Anonymous Cloudflare Quick Tunnels for the remote-access data plane

Zeus desktop exposes itself to a paired mobile client by spawning `cloudflared` as a subprocess that opens an anonymous Quick Tunnel against `trycloudflare.com`. No third-party account exists in the user's mental model, and no per-byte cost lands on the maintainer's infrastructure.

## Considered options

- **ngrok free tier** — rejected. The user must sign up at ngrok.com and paste an authtoken, which kills the "open app, scan QR, online" first-run UX.
- **Cloudflare Tunnel on the maintainer's CF account** (the original `feature_tunnel_broker` design) — rejected. Every user's data bytes would flow through the `openhpsdrzeus.com` Cloudflare account; the maintainer would bear both cost and abuse risk as the user base grows.
- **User's own Cloudflare account with a named tunnel** — rejected. The signup and `cloudflared` auth flow is equivalent friction to ngrok.
- **Tailscale Funnel / ZeroTier mesh** — rejected. Mesh-VPN onboarding requires installing a separate client and authenticating to an external account before the user can even reach the radio. Heavier than any HTTP-tunnel option for the common case.

## Consequences

- Each session gets a different ephemeral URL (`xyz-abc.trycloudflare.com`). The broker handles stability via slug → current-URL redirection — see [0002](./0002-broker-as-identity-redirector.md).
- Cloudflare officially labels Quick Tunnels "for testing and development only." There is no SLA, and Cloudflare can rate-limit or remove the feature unilaterally. We accept this risk without a hedge in v1.
- Cloudflare retired TOS Section 2.8 (the audio/video CDN restriction) for Tunnel; real-time WebSocket audio is no longer policy-blocked.
- WebSocket and SignalR are supported on Quick Tunnels; Zeus's existing `UseWebSockets` configuration with a 20s keepalive already aligns with Cloudflare's idle-close behavior.
- Quick Tunnels enforce a 200-concurrent-request cap, which is comfortable for one operator with one mobile client.
- `cloudflared` must be available on the user's machine — see [0004](./0004-bundle-cloudflared-in-installer.md).

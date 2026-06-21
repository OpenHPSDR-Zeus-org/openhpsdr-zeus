# WebRTC peer-to-peer as the remote-access data plane

**Supersedes [0001](./0001-data-plane-cloudflare-quick-tunnels.md).** Remote clients reach
the operator's `Zeus.Server` over a **WebRTC** connection — a direct UDP path negotiated by
ICE, with a TURN relay used only when NAT/CGNAT defeats the direct path. Audio rides an Opus
media track; spectrum/IQ/display frames ride an unreliable-unordered DataChannel; VFO and
control ride a reliable-ordered DataChannel. The maintainer's domain never carries session
bytes.

## Why we pivoted away from Quick Tunnels (0001)

The Quick Tunnel design routed **every** byte — each 20 ms audio block, every 60 Hz display
frame, every VFO command — through Cloudflare's edge and back. For a real-time SDR client the
operative requirement is *extremely low latency*, and a fixed-POP HTTP relay adds a round trip
plus TCP head-of-line blocking to all traffic, including connections that are physically close.
WebRTC inverts this: the common case is a direct UDP path with only physical-path RTT; a relay
is the exception, not the rule.

## Considered options

- **Cloudflare Quick Tunnels (0001)** — superseded. Relays 100% of traffic over TCP/WebSocket
  through a fixed edge; "testing/development only" per Cloudflare, no SLA, ~200 concurrent-request
  cap, ephemeral URLs. Worst latency profile of the options.
- **Per-user named Cloudflare Tunnel** — rejected, same relay-latency problem as Quick Tunnels
  plus per-user `cloudflared` auth friction.
- **Tailscale / WireGuard mesh** — rejected for the *browser* client: there is no WireGuard
  inside a web page, so a browser/webview remote client cannot establish the tunnel. (Excellent
  for a future *native* client; not this one.)
- **WebRTC P2P + TURN fallback** — chosen. Native in every browser, traverses NAT/CGNAT without
  router config, direct UDP for ~80% of home connections, and gives us Opus + jitter buffer +
  FEC for the audio path for free.

## Consequences

- `Zeus.Server` gains a WebRTC peer via **SIPSorcery** (pure-C#, .NET 10, runs on linux-arm64 /
  Raspberry Pi). Opus is encoded in managed/DSP code so we avoid the native `SIPSorceryMedia.FFmpeg`
  dependency on arm64/Pi. SIPSorcery's BSD-3 license carries a non-standard ethical-use clause —
  flagged for license review.
- The existing `/ws` binary `WireFormat` frames are reused verbatim **over DataChannels**, so the
  frame contract and all decoders survive; only the transport changes. See the transport map in
  [docs/designs/remote-access-webrtc.md](../designs/remote-access-webrtc.md).
- DTLS-SRTP gives transport encryption for free; remote access is no longer the
  "trusted-LAN, no-auth" model the current `/ws` assumes — auth is added (see [0006](./0006-broker-signaling-turn-callsign.md)).
- ~15–30% of home-to-home sessions (CGNAT-heavy) fall back to TURN, adding ~20–80 ms; budget for it.
- TURN relay is **Cloudflare Realtime TURN** (anycast, $0.05/GB after 1,000 GB/mo free) — see [0006](./0006-broker-signaling-turn-callsign.md).
- **[0004](./0004-bundle-cloudflared-in-installer.md) (bundle `cloudflared`) is no longer required**
  by the primary path. A Quick Tunnel may be retained later as a last-resort transport for the rare
  environment where even TURN-over-443 is blocked; that is an additive fallback, not the data plane.

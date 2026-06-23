# Protocol-2 second-master relay chatter (hardware safety)

## Symptom

On a co-located ANAN-G2 / Saturn all-in-one (the radio's own SBC runs
`saturn-go` + `p2app` AND hosts Zeus), connecting Zeus while the native stack is
actively driving the radio makes **every relay in the radio chatter** — the
BPF/LPF band filters, the antenna relays, and the T-R matrix all buzz rapidly.
The relay inrush can brown out the shared PSU and reboot the host.

Observed 2026-06-23 on a CM5 Saturn all-in-one at `192.168.1.25`.

## Root cause

Protocol 2 does not authenticate the controller — the radio's FPGA obeys
whoever last commanded it. With two controllers attached, **both** send
`CmdHighPriority` (Zeus's keepalive runs at 10 Hz; see
`Zeus.Protocol2/Protocol2Client.cs` `KeepaliveLoop`) carrying **different**
band / antenna / ALEX-relay selections. `p2app` forwards whichever packet
arrived last to the FPGA, so the relay matrix flip-flops on every packet.

A **single** Zeus master is fine: `SendCmdHighPriority` builds the ALEX relay
words from constant fields, so a steady keepalive re-sends byte-identical relay
words and nothing moves. The chatter **requires a second, disagreeing
controller**.

## Guard

`/api/connect/p2` (`Zeus.Server.Hosting/ZeusEndpoints.cs`) unicast-probes the
target radio before opening the relay-bearing high-priority stream and refuses
(HTTP 409) if discovery reports the radio **Busy** (status byte `0x03` — another
controller owns it). The probe is
`Zeus.Protocol2.Discovery.IRadioDiscovery.ProbeAsync` — a targeted unicast
discovery to the radio IP (not a broadcast), so it works for the co-located case
where the radio is the host's own address.

Key design rules, do not regress:

- **Honour the discovery `Busy` flag, not "port 1025 is bound."** Port 1025
  being held is *also* the legitimate sole-master co-located case (`p2app` owns
  1025, Zeus binds an ephemeral local port — see the
  `AddressAlreadyInUse → bind(port 0)` fallback in `Protocol2Client.ConnectAsync`).
  Blocking on a held 1025 would break the verified-working sole-master workflow.
  The `Busy` flag is what actually distinguishes "another controller is driving
  it" from "I'm the only master on a shared host."
- **Fail open.** A radio that doesn't answer the probe is *not* blocked — the
  guard only refuses on a positive `Busy` reply, never on silence, so it can
  never strand a legitimate connect.
- **Do not touch the connect socket.** The probe only reads discovery; it must
  not weaken the co-located ephemeral-port bind.
- **Takeover path uses `force`.** The operator takes over deliberately via
  Reclaim (`/api/radios/reclaim` sends a protocol stop that drops the other
  owner), then re-connects with `ConnectRequest.Force = true` so the
  still-settling radio's lingering `Busy` status doesn't re-block the connect.

## Verifying on the co-located box

Test as **sole master only**. Stop the native stack first
(`systemctl stop saturn-go`), confirm exclusive control, then connect. **Never**
reproduce the dual-master case — it stresses the relays and can brown out the
box.

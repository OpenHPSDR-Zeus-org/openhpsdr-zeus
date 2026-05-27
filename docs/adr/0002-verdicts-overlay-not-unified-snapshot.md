---
status: accepted
---

# Health Frame carries verdicts only, not a unified snapshot

The Audio Chain Monitor publishes one new WebSocket binary frame type,
`AudioChainHealth` (`MsgType = 0x32`), at ~2 Hz, carrying only
**Verdicts** — severity, message, apply target, deeplink — keyed by
**Stage ID**. Raw stage readings stay on their existing frames
(`TxMetersV2` 0x16, `PaTemp` 0x17, `RxMetersV2` 0x19, etc.). The **Factory Widget** joins verdicts and raw numbers in the
frontend by Stage ID. We rejected the unified-snapshot alternative
(one fat DTO bundling every reading + verdict) because duplicating
numbers already on the wire bloats the WS payload, couples two wire
contracts that change on different cadences (raw meters at 10–30 Hz
hardware-paced; verdicts at human-paced ~2 Hz), and forces the rule
engine onto the hot meter-publish path. Verdicts-only keeps the
existing hot path untouched and gives third-party consumers a clean
"chain health" subscription that's stable independent of the raw
meter format. Frame type `0x32` lives in the new 0x3x
"control-plane feedback" nibble alongside `CwEngineStatus` /
`CwDecodedText`; older clients ignore unknown types.

## Consequences

- The widget must join across at least three frame streams
  (`TxMetersV2`, `PaTemp`, `AudioChainHealth`). Stage ID is the
  load-bearing join key; renaming a Stage ID is a wire break.
- Transient frames may show a verdict-without-number or
  number-without-verdict at startup or during socket reconnect; widget
  handles this gracefully (renders the half it has).
- `tx.wire.*` Stages (drive byte, IQ peak, packet rate) do NOT get
  their own frame type — the Monitor reads those fields in-process
  from `RadioService` / `Protocol1Client` and ships only the resulting
  verdict.

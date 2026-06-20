## Advanced: Experimental & Opt-in DSP

Zeus is conservative about the way your radio sounds. When the project explores a
new way of doing part of the signal processing, it does **not** quietly change the
audio you already rely on. Instead, the new behaviour is offered as an **opt-in
DSP candidate** that sits *alongside* the established, proven behaviour. Your
receive and transmit audio keep working exactly as they always have unless you
deliberately choose to try a candidate — and even then, you can switch back at any
time.

This chapter explains that system and the candidates it covers, so you understand
what you are turning on if you decide to experiment.

### What an "opt-in DSP candidate" is

Think of a candidate as a clearly-labelled alternate recipe for one stage of the
audio chain. The current recipe remains the default. A candidate becomes the new
default only after it has gathered enough real-world, repeatable evidence to prove
it is genuinely better — never on the strength of a single good-sounding session.
This is the principle behind Zeus's DSP work: a change that merely *seems* nicer
in a short, noisy listening test cannot be mistaken for a measured improvement.

For you as an operator, three things always hold:

- **Defaults are unchanged.** Your receive and transmit audio behave exactly as in
  the standard configuration unless you opt into a candidate.
- **Candidates are experimental and default to off.** They are explicitly marked
  as experimental, and any candidate can be revised based on what on-air use shows.
- **You are in control.** Engaging or disengaging a candidate is your choice, and
  the established behaviour is always one step away.

### Receive: the "stable-speech" leveler

The stable-speech candidate targets the loudness "pumping" you can sometimes hear
on received voice — the sense that the audio keeps surging up and easing back. It
aims to hold speech at a more even level **without** clamping down on natural peaks
or adding hard limiting, so weak syllables stay intact while the overall level
wanders less. If you listen to a lot of SSB voice and find the automatic level a
little restless, this is the candidate to try.

### Transmit: the "headroom-trim" option

On the transmit side, the headroom-trim candidate adjusts the amount of output
headroom in the TX audio path.

**Important safety behaviour:** when **PureSignal is armed, this candidate is
automatically bypassed.** It will not alter your transmit path while PureSignal is
active, so it can never interfere with PureSignal's own calibration and
correction. This is consistent with Zeus's standing PureSignal rule — PureSignal
only ever arms when *you* arm it, every session — and the headroom-trim option
respects that by stepping out of the way whenever PureSignal is in control.

### How a candidate "graduates"

A candidate stays experimental until accumulated, measured evidence across real
operating shows it is a true improvement — at which point it can become the
default, with the previous behaviour still available. Because Zeus runs on a wide
range of boards, that evidence is gathered cautiously and across radios, so a
candidate that helps one setup is not pushed onto everyone before it has earned it.
The result is a DSP path that keeps improving without ever surprising you with a
change you did not ask for.

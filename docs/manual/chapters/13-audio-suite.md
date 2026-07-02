## The Audio Suite & Plugins

The Audio Suite is where you shape your transmit audio before it ever leaves the radio. Instead of a single fixed processor, Zeus runs your voice through a chain of small, independent processing blocks — a noise gate, an equaliser, a compressor, an exciter, a bass enhancer, and a reverb — that you can add, remove, reorder, and switch in and out individually. It works like a small studio rack built right into Zeus, and everything runs inside Zeus itself (no separate program, no virtual audio cables), so there is nothing to route by hand.

This chapter covers what each part does, where to find it, and how to use it to get a clean, punchy signal on the air.

### What it is and where the audio goes

The Audio Suite is a **transmit** processing chain. Your microphone audio flows through each block in the chain, in order, top to bottom, before it reaches the radio's DSP and goes out as RF. Because the order is real signal flow — not just a cosmetic list — moving a block actually changes how your audio is processed.

A couple of important boundaries to keep in mind:

- The suite acts on the **plugin chain only**. Zeus's built-in WDSP processing — including the CFC (Continuous Frequency Compressor) — sits *downstream* of the suite and is not affected by it. Bypassing the whole suite does not touch CFC.
- There is also a separate **RX Audio Suite** for receive-side effects, which works the same way but inserts VST plugins into your *receive* audio. The RX suite only hosts VST3 plugins (see below); the six built-in voice processors are transmit-side.

### Getting the plugins: Download Audio Suite

On a fresh install the suite is empty — Zeus ships the framework, but the actual processing blocks are installed plugins. The fastest way to get all six is the **Download Audio Suite** button on the Audio Tools panel.

One click installs the complete voice chain, in the conventional signal-flow order, from the official Zeus plugin repository:

| Order | Block | What it does |
|-------|-------|--------------|
| 1 | **Noise Gate** | Mutes the channel when you're not talking, so room noise and hiss don't go out between words. Has a threshold, hold timer, attack/release, a range control, and an OPEN / HOLD / CLOSED state indicator. |
| 2 | **EQ** (10-band parametric) | Tone shaping. Cut and boost specific frequency bands; a live spectrum display sits behind the curve, with input and output gain stages. |
| 3 | **Compressor** | Evens out your level so quiet and loud passages sit closer together — more consistent, more present audio. |
| 4 | **Aural-Exciter** | Adds high-frequency harmonics for clarity and "air" without simply turning up the treble. |
| 5 | **Bass Enhancer** | Adds low-end warmth and body. |
| 6 | **Reverb** | A touch of ambience. Use sparingly on the air. |

As each plugin installs you'll see a progress list with a tick, a "skip" mark for anything already present, or a red mark if one failed. Anything you already have is skipped — to *replace* a version, uninstall it first from the Plugins admin, then download the suite again.

**Restart required.** New plugins register their processing and controls only when the backend starts. After the install finishes, Zeus shows a "restart required" notice. Close Zeus and start it again; the chain appears on next launch. There is no auto-restart — you decide when.

### Opening the suite and reading the rack

Open the suite from the **Audio Suite** button on the Audio Tools panel. It appears as a floating window titled **TX Audio Suite** that you can drag by its header and resize from any edge or corner. Press **Escape** to close it. (On a desktop install it can also render inline within the Audio Tools settings pane.)

The window has three main areas:

- **IN / OUT meters** at the top — two horizontal bars showing the level entering and leaving the whole chain. Green is nominal, yellow is hot, red means you're near clipping; a peak tick holds briefly so you can catch transients. **These read silence when idle** — the chain only processes during transmit or preview, so resting bars at the floor are normal, not a fault.
- **The chain strip** — a row of numbered chips, one per block, in signal-flow order. This *is* your chain.
- **The detail pane** — click any chip to load that block's full controls below. Exactly one block's panel is shown at a time, so the window stays compact instead of growing into a tall stack.

On the left is a collapsible **Plugins** browser listing everything installed but not currently in the chain (the "Available" list), with a search box.

### Building and ordering your chain

- **Add a block:** click the **+** beside it in the Available list, or drag it onto the chain strip.
- **Reorder:** grab a chip by its dotted drag handle and drop it where you want it. The new order takes effect immediately as real signal flow.
- **Remove from chain (park):** click the **×** on a chip. This stops the block processing and moves it back to the Available list — it stays installed, so you can add it back any time. This is non-destructive.
- **Uninstall:** to delete a plugin from Zeus entirely, use the trash control in the Available list. It arms on the first click and only deletes on a second click within a few seconds. The six built-in voice blocks aren't removed this way; the trash is for VST3 plugins you've added.

A sensible starting order is the default install order above: gate first to clean up, EQ and dynamics in the middle, enhancers and reverb last. When in doubt, start there and adjust by ear.

### Bypass: the A/B test, and the master switch

Every block in the chain has its own **bypass**. Bypassing a block leaves it loaded and configured but takes it out of the signal path, so you can flip it on and off to hear exactly what it's doing — a clean before/after A/B without losing your settings. You can do this mid-transmit. Use it constantly while dialling in: bypass a block, listen, re-enable, decide whether it's actually helping.

There is also a **Master Bypass** that disengages the entire suite in one click instead of toggling six blocks. **On a fresh install the master bypass is ON, meaning the chain is inert** — so a brand-new operator isn't surprised by an unfamiliar processing chain reshaping their first transmission before they've set anything up. Turn it off when you're ready to use your chain. This state survives restarts. Remember: master bypass acts on the plugin chain only — your WDSP CFC keeps running regardless.

### Hearing it before you key up: Preview

You don't have to transmit to hear your chain. The **Preview** toggle in the suite-window header runs your full transmit-monitor path and mixes the processed audio into your **RX playback** — your headphones or speakers — so you hear exactly what would go on the air. The IN/OUT meters animate with Preview on even with the radio not keyed, so you can stage gain and set dynamics off the air.

One rule to know: preview follows your receive audio. **Muting RX also mutes preview** (the "share with receive" convention). If preview goes quiet, check that RX isn't muted. Preview is unavailable in some host modes; if the button is greyed out, that's why.

### Profiles: saving chains for different operating

Above the chain you can save the whole chain — the blocks, their order, and their settings — as a **named profile**. Use the **+** to capture the current chain under a new name (for example "Ragchew" or "DX"), **Save** to overwrite the selected profile in place, and **Delete** to remove one. Selecting a profile applies it instantly. This lets you keep one chain tuned for relaxed local audio and another for cutting through a pileup, and switch between them in a click. (The RX suite keeps its own separate profiles.)

### VST3 / Audio Unit hosting and the plugin system

Beyond the built-in blocks, Zeus can host third-party audio plugins **in-process** — the same plugins you'd use in a digital audio workstation — on both the transmit chain and the RX chain. This is a desktop feature, and it works across platforms:

- **Windows** — VST3 plugins, from the standard VST3 folders.
- **macOS** — VST3 plugins *and* native **Audio Units (AU)**, so the Apple-format plugins you already own show up too.
- **Linux** — native, in-process VST3 hosting with working plugin editors (X11).

Transmit plugin hosting is now **enabled by default**, so your TX chain is ready to take plugins without flipping a setting first.

To bring plugins in, use the scan controls at the bottom of the Plugins browser:

- **Scan** sweeps the common plugin locations for your platform in one click — VST3 everywhere, plus Audio Units on macOS; folders that don't exist are simply skipped.
- **Scan Both Suites** does the same sweep for both the TX and RX suites.
- **+ Add folder** scans one specific folder you name.

A hosted plugin chip in the chain carries a small format tag (**VST** / **AU**) and a star you can use to mark favourites (favourites sort to the top of the browser). Because a plugin's real interface is a native operating-system window — not browser HTML — Zeus can't draw it inside the suite window. Instead, selecting a plugin chip opens its **real editor as a separate desktop window**, exactly like a standalone host, with an Open Editor / Close Editor button in the detail pane. The plugin processes your audio whether or not its editor window is open; closing the editor just hides the GUI. If a plugin ever misbehaves, the host process is self-healing — it recovers on its own rather than taking the radio down with it.

The RX suite header shows a **VST** status pill — ON when receive VSTs are processing, IDLE when the engine is available but unused, OFF when no VST engine is installed — plus its own Bypass toggle for the whole receive chain. On Windows, when the engine is missing the RX Audio row in Settings → Audio Tools now offers its own **Download VST Engine** button, so you can enable receive VSTs without first switching your TX route to VST.

### Practical tips

- **Start with master bypass off only when you're ready.** It ships ON for a reason; build and audition your chain, then engage it.
- **Use per-block bypass as your ears' A/B switch.** If you can't clearly hear a block helping, you probably don't need it.
- **Watch the OUT meter.** Keep it out of the red. Each block can add gain; the goal is consistent, clean level into the radio, not the loudest possible signal.
- **Don't double up on processing.** If you already process your voice upstream (an external mixer or audio software), keep the suite light — and remember Zeus's CFC is still downstream of everything here.
- **Build the chain, restart, then dial in.** New plugins only come alive after a restart; once they're in, all your tuning happens live with Preview.

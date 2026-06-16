// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import type { ComponentType } from 'react';
import { HeroPanel } from './panels/HeroPanel';
import { VfoPanel } from './panels/VfoPanel';
import { SMeterPanel } from './panels/SMeterPanel';
import { QrzPanel } from './panels/QrzPanel';
import { AzimuthPanel } from './panels/AzimuthPanel';
import { RotatorCompassPanel } from './panels/RotatorCompassPanel';
import { RotatorDialPanel } from './panels/RotatorDialPanel';
import { DspFlexPanel } from './panels/DspFlexPanel';
import { CwPanel } from './panels/CwPanel';
import { CwDecoderPanel } from './panels/CwDecoderPanel';
import { LogbookPanel } from './panels/LogbookPanel';
import { TxMetersPanel } from './panels/TxMetersPanel';
import { TxFidelityPanel } from './panels/TxFidelityPanel';
import { TxPanel } from './panels/TxPanel';
import { FilterRibbonPanel } from './panels/FilterRibbonPanel';
import { FilterPresetsPanel } from './panels/FilterPresetsPanel';
import { PsFlexPanel } from './panels/PsFlexPanel';
import { BandPanel } from './panels/BandPanel';
import { ModePanel } from './panels/ModePanel';
import { StepPanel } from './panels/StepPanel';
import { MeterGroupPanel } from '../components/meter-group/MeterGroupPanel';
import { AnalogMeterPanel } from './panels/AnalogMeterPanel';
import { WavRecorderPanel } from './panels/WavRecorderPanel';
import { HamClockPanel } from './panels/HamClockPanel';
import { SpotsPanel } from './panels/SpotsPanel';
import { SpaceWeatherPanel } from './panels/SpaceWeatherPanel';
import { UrlEmbedPanel } from './panels/UrlEmbedPanel';

export type PanelCategory =
  | 'spectrum'
  | 'vfo'
  | 'meters'
  | 'dsp'
  | 'log'
  | 'tools'
  | 'amplifiers'
  | 'tuners'
  | 'controls'
  | 'switches'
  | 'plugins';

/** Human-friendly category labels for the Add Panel modal's left rail. The
 *  rail shows these in a fixed order; "All" is rendered separately as a
 *  passthrough chip. */
export const PANEL_CATEGORIES: ReadonlyArray<PanelCategory> = [
  'spectrum',
  'vfo',
  'meters',
  'dsp',
  'log',
  'tools',
  'amplifiers',
  'tuners',
  'controls',
  'switches',
  'plugins',
];
export const PANEL_CATEGORY_LABELS: Record<PanelCategory, string> = {
  spectrum: 'Spectrum',
  vfo: 'VFO',
  meters: 'Meters',
  dsp: 'DSP',
  log: 'Log',
  tools: 'Tools',
  amplifiers: 'Amplifiers',
  tuners: 'Tuners',
  controls: 'Controls',
  switches: 'Switches',
  plugins: 'Plugins',
};

const VALID_PANEL_CATEGORIES = new Set<string>(PANEL_CATEGORIES);

/** Most panels render with no props — the workspace tile renders them as
 *  `<def.component />`. Multi-instance panels with per-instance config
 *  (just `meters` today) take a typed prop pair instead; `PanelTile` knows
 *  to switch on `def.id === 'meters'` for that wiring. Headerless panels
 *  receive `onRemove` so the close button they own can drop the tile. */
export type PanelComponentProps = { onRemove?: () => void };

export interface PanelDef {
  id: string;
  name: string;
  category: PanelCategory;
  tags: string[];
  component: ComponentType<PanelComponentProps>;
  /** When true, the Add Panel modal allows duplicates and the workspace
   *  store mints a unique tile uid per instance so each tile holds its own
   *  per-instance config blob. Default false (single-instance, current
   *  behaviour for every panel except `meters`). */
  multiInstance?: boolean;
  /** When true, PanelTile skips rendering TileChrome and the
   *  workspace-tile-body wrapper. The panel body fills the tile and is
   *  responsible for drawing its own header (if any). It must include an
   *  element with class `.workspace-tile-header` so react-grid-layout can
   *  pick up dragging, and a `.workspace-tile-close` button wired to the
   *  injected `onRemove` prop. Useful for panels that already manage rich
   *  toolbars (Meters has gear / library / settings drawers; Panadapter has
   *  band/zoom/cursor strip; Azimuth has SP/LP toggles). */
  headerless?: boolean;
  /** Width cap in 12-col grid units. When set, RGL won't let the operator
   *  drag the tile any wider — the closest analogue to "anchor: top, right"
   *  in a Windows-Forms-style designer. Right-column stack panels (vfo /
   *  smeter / dsp / txmeters / azimuth / tx) cap at 3 so they grow only in
   *  height, never sprawling into the panadapter column. Omit for
   *  freely-sizable panels. */
  maxW?: number;
  /** Height cap in grid rows. Optional ceiling on vertical growth.
   *  Omit for freely-sizable panels. */
  maxH?: number;
  /** Width floor in 12-col grid units. Below this the panel's content stops
   *  being legible, so RGL refuses to let the operator drag it narrower and
   *  the responsive auto-fit treats it as the minimum span. Omit to fall
   *  back to the workspace-global WORKSPACE_TILE_MIN_W. */
  minW?: number;
  /** Height floor in grid rows. Same contract as minW on the vertical axis —
   *  dense panels (TX meters, DSP, logbook) set this so the viewport auto-fit
   *  can't crush them below the height their content needs to read. Omit to
   *  fall back to WORKSPACE_TILE_MIN_H. */
  minH?: number;
}

// Panel registry: maps component-id strings (used in the flexlayout JSON model)
// to panel metadata and the React component that renders the panel body.
// Phase 3 will add an "Add Panel" modal that reads this registry.
export const PANELS: Record<string, PanelDef> = {
  hero: {
    id: 'hero',
    name: 'Panadapter · World Map',
    category: 'spectrum',
    tags: ['panadapter', 'waterfall', 'spectrum', 'map'],
    component: HeroPanel,
    // Headerless: HeroPanel draws its own .workspace-tile-header so the
    // single strip can host the zoom slider, rotator chips (SP/LP/BEAM),
    // ⌥ map-mode hint, and HZ/PX readout — instead of stacking those on
    // top of the default TileChrome (the old "double header").
    headerless: true,
    // Keep enough room for the header, splitter, and both canvases while
    // still letting operators dock the spectrum stack into a short strip.
    minW: 8,
    minH: 8,
  },
  vfo: {
    id: 'vfo',
    name: 'Frequency · VFO',
    category: 'vfo',
    tags: ['frequency', 'vfo', 'tuning'],
    component: VfoPanel,
    maxW: 6,
    minW: 4,
    minH: 6,
  },
  smeter: {
    id: 'smeter',
    name: 'S-Meter',
    category: 'meters',
    tags: ['signal', 'meter', 'rx', 'smeter'],
    component: SMeterPanel,
    maxW: 6,
    minW: 4,
    minH: 4,
  },
  qrz: {
    id: 'qrz',
    name: 'QRZ Lookup',
    category: 'tools',
    tags: ['qrz', 'callsign', 'lookup', 'station'],
    component: QrzPanel,
    minW: 6,
    minH: 8,
  },
  azimuth: {
    id: 'azimuth',
    name: 'Azimuth Map',
    category: 'tools',
    tags: ['azimuth', 'map', 'bearing', 'great-circle'],
    component: AzimuthPanel,
    maxW: 6,
    minW: 4,
    minH: 10,
  },
  rotatorcompass: {
    id: 'rotatorcompass',
    name: 'Rotator Compass',
    category: 'tools',
    tags: ['rotator', 'compass', 'bearing', 'heading', 'sp', 'lp', 'map'],
    component: RotatorCompassPanel,
  },
  rotatordial: {
    id: 'rotatordial',
    name: 'Rotator Dial',
    category: 'tools',
    // No 'azimuth' tag — that search term scopes to the dedicated Azimuth
    // Map panel. `bearing` + `heading` already cover the same semantic
    // for the dial without overlapping that filter.
    tags: ['rotator', 'compass', 'dial', 'bearing', 'heading'],
    component: RotatorDialPanel,
  },
  dsp: {
    id: 'dsp',
    name: 'DSP',
    category: 'dsp',
    tags: ['dsp', 'noise', 'filter', 'nr', 'anf'],
    component: DspFlexPanel,
    maxW: 6,
    minW: 4,
    minH: 6,
  },
  cw: {
    id: 'cw',
    name: 'CW Keyer',
    category: 'tools',
    tags: ['cw', 'morse', 'keyer', 'wpm'],
    component: CwPanel,
    minW: 6,
    minH: 6,
  },
  cwdecoder: {
    id: 'cwdecoder',
    name: 'CW Decoder',
    category: 'tools',
    tags: ['cw', 'morse', 'decoder', 'receive'],
    component: CwDecoderPanel,
    // Headerless: CwDecoderPanel draws its own TileChrome (carrying the
    // ON/OFF toggle in the right slot). Without this flag the host renders
    // a second default TileChrome on top, producing a duplicated window
    // header — and the panel's own close button goes dead because PanelTile
    // only injects onRemove to headerless panels.
    headerless: true,
    minW: 6,
    minH: 6,
  },
  logbook: {
    id: 'logbook',
    name: 'Logbook',
    category: 'log',
    tags: ['log', 'qso', 'logbook', 'adif'],
    component: LogbookPanel,
    minW: 6,
    minH: 8,
  },
  txmeters: {
    id: 'txmeters',
    name: 'TX Stage Meters',
    category: 'meters',
    tags: ['tx', 'power', 'swr', 'alc', 'meters'],
    component: TxMetersPanel,
    maxW: 6,
    // The immersive cluster stacks three gauge sections + a footer; below
    // ~6 legacy rows the lower sections clip behind the tile's inner
    // scrollbar. minW must stay BELOW maxW — when they're equal RGL pins the
    // width and the tile can't be resized sideways at all.
    minW: 4,
    minH: 12,
  },
  txfidelity: {
    id: 'txfidelity',
    name: 'TX Fidelity',
    category: 'meters',
    tags: ['tx', 'audio', 'fidelity', 'broadcast', 'mic', 'alc', 'leveler', 'cfc'],
    component: TxFidelityPanel,
    maxW: 6,
    minW: 4,
    minH: 20,
  },
  tx: {
    id: 'tx',
    name: 'TX Chain',
    category: 'controls',
    tags: ['tx', 'drive', 'tune', 'mic', 'mic-gain', 'power', 'filter', 'bandpass'],
    component: TxPanel,
    maxW: 6,
    minW: 4,
    minH: 8,
  },
  filter: {
    id: 'filter',
    name: 'Bandwidth Filter',
    category: 'dsp',
    tags: ['filter', 'bandwidth', 'passband', 'ribbon'],
    component: FilterRibbonPanel,
    minW: 6,
    minH: 4,
  },
  filterpresets: {
    id: 'filterpresets',
    name: 'Filter Presets',
    category: 'dsp',
    tags: ['filter', 'presets', 'bandwidth', 'passband', 'var', 'custom'],
    component: FilterPresetsPanel,
    // Caps to the right-column stack width (like smeter/tx/txmeters) so the
    // preset card tucks under the TX meters instead of sprawling into the
    // panadapter column. minW stays below maxW or RGL pins the width.
    maxW: 6,
    minW: 4,
    minH: 6,
  },
  ps: {
    id: 'ps',
    name: 'PureSignal',
    category: 'tools',
    tags: ['puresignal', 'ps', 'tx', 'predistortion', 'linearization', 'twotone'],
    component: PsFlexPanel,
    minW: 6,
    minH: 8,
  },
  band: {
    id: 'band',
    name: 'Band',
    category: 'controls',
    tags: ['band', 'frequency', 'hf', 'tuning'],
    component: BandPanel,
    minW: 6,
    minH: 4,
  },
  mode: {
    id: 'mode',
    name: 'Mode',
    category: 'controls',
    tags: ['mode', 'modulation', 'ssb', 'cw', 'am', 'fm'],
    component: ModePanel,
    minW: 4,
    minH: 4,
  },
  step: {
    id: 'step',
    name: 'Tuning Step',
    category: 'controls',
    tags: ['step', 'tuning', 'frequency', 'increment'],
    component: StepPanel,
    minW: 4,
    minH: 4,
  },
  metergroup: {
    id: 'metergroup',
    name: 'Meter Group',
    category: 'meters',
    tags: ['meters', 'rx', 'tx', 'signal', 'power', 'agc', 'alc', 'group', 'row', 'column'],
    component: MeterGroupPanel,
    multiInstance: true,
    headerless: true,
  },
  wavrecorder: {
    id: 'wavrecorder',
    name: 'Tape Deck · WAV Recorder',
    category: 'tools',
    tags: ['recorder', 'wav', 'tape', 'record', 'playback', 'audio', 'reel'],
    component: WavRecorderPanel,
    maxW: 8,
  },
  analogmeter: {
    id: 'analogmeter',
    name: 'Analog S-Meter',
    category: 'meters',
    tags: ['analog', 'meter', 'smeter', 's-meter', 'signal', 'rx', 'tx', 'power', 'swr', 'needle'],
    component: AnalogMeterPanel,
    headerless: true,
  },
  spots: {
    id: 'spots',
    name: 'POTA / SOTA Spots',
    category: 'tools',
    tags: ['spots', 'pota', 'sota', 'activation', 'dx', 'cluster', 'tune', 'park', 'summit'],
    component: SpotsPanel,
    minW: 6,
    minH: 8,
  },
  hamclock: {
    id: 'hamclock',
    name: 'HamClock',
    category: 'tools',
    tags: ['hamclock', 'dashboard', 'propagation', 'dx', 'cluster', 'satellite', 'pota', 'sota', 'space weather', 'map'],
    component: HamClockPanel,
    // Wants the whole workspace — it's a full dashboard embedded as an iframe.
    minW: 8,
    minH: 16,
  },
  spacewx: {
    id: 'spacewx',
    name: 'Solar · Space Weather',
    category: 'tools',
    tags: ['solar', 'space weather', 'propagation', 'sfi', 'flux', 'sunspots', 'k-index', 'a-index', 'aurora', 'muf', 'bands', 'n0nbh', 'hamqsl'],
    component: SpaceWeatherPanel,
    minW: 6,
    minH: 10,
  },
  urlembed: {
    id: 'urlembed',
    name: 'URL Embed',
    category: 'tools',
    tags: ['url', 'embed', 'iframe', 'web', 'browser', 'page', 'link', 'site'],
    component: UrlEmbedPanel,
    // Multi-instance so an operator can pin as many pages as they like —
    // each tile holds its own assigned URL in instanceConfig.
    multiInstance: true,
    // Headerless: the panel owns its header strip so the address bar can
    // live alongside the drag grip and close X.
    headerless: true,
    minW: 6,
    minH: 8,
  },
};

// Plugin-contributed panels. Loaded at app startup by pluginRuntime; the
// workspace and AddPanelModal go through these helpers instead of reading
// PANELS directly so plugin panels show up in both surfaces.

import { listRegisteredPanels } from '../plugins/runtime/pluginRuntime';

function pluginPanelDef(p: import('../plugins/runtime/pluginRuntime').RegisteredPluginPanel): PanelDef {
  const category = (VALID_PANEL_CATEGORIES.has(p.category)
    ? p.category
    : 'plugins') as PanelCategory;
  return {
    id: p.panelId,
    name: p.title,
    category,
    tags: ['plugin', p.pluginId],
    component: p.component as ComponentType<PanelComponentProps>,
  };
}

export function getPanelDef(id: string): PanelDef | undefined {
  const builtIn = PANELS[id];
  if (builtIn) return builtIn;
  const plugin = listRegisteredPanels().find((p) => p.panelId === id);
  return plugin ? pluginPanelDef(plugin) : undefined;
}

export function getAllPanels(): PanelDef[] {
  return [
    ...Object.values(PANELS),
    ...listRegisteredPanels().map(pluginPanelDef),
  ];
}


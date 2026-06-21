// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// WebGPU shaded-relief waterfall — fullscreen per-fragment renderer.
//
// The view is straight-down, so there is no 3D mesh to draw: a single fullscreen
// triangle covers the panel and the FRAGMENT shader does everything — per-pixel
// colour resampled from the history at full resolution, plus a 4-tap hillshade
// for the topographic relief. This is the most efficient form for a 2D shaded-
// relief waterfall: no vertex/index buffers, no depth buffer, no mesh, no camera
// matrices — ~5 texture taps per pixel and nothing per-vertex.
//
// Seamless zoom/pan, no remap pass: each history row is stamped with the centre
// /span it was captured at (rowGeom storage buffer); every row maps itself to
// the current animated view, so a zoom scales and a retune slides per row with
// the whole history staying frequency-aligned.
//
// Low-resource notes:
//   • The history texture is allocated but NOT seeded — WebGPU zero-inits
//     textures, and unwritten rows are gated to the floor colour by rowGeom
//     (hzPerPixel 0) and validRows, so we skip the multi-MB per-reseed upload
//     the mesh version paid on every width/zoom change.
//   • Backing-store DPR is clamped to 1 by the component.
//   • Draws are coalesced to one per frame by the shared draw bus (component).

import { lutFor, type RenderColormapId } from '../colormap';

const HISTORY_ROWS = 4096;

export type HeightfieldRenderer = {
  /** Upload the newest spectral row (dB per bin), tagged with the centre/span it
   *  was captured at. Re-allocates on a width change. */
  pushRow: (rowDb: Float32Array, centerHz: number, hzPerPixel: number) => void;
  /** Render the history. `viewCenterHz` / `viewHzPerPixel` are the animated view
   *  (view-center.ts / view-zoom.ts); when supplied the per-row sampling glides
   *  so pan/zoom are seamless. Null renders against the latest captured view. */
  draw: (
    dbMin: number,
    dbMax: number,
    viewCenterHz?: number | null,
    viewHzPerPixel?: number | null,
  ) => void;
  resize: (w: number, h: number) => void;
  setColormap: (id: RenderColormapId) => void;
  /** 0..1 hillshade depth (relief contrast). */
  setReliefDepth: (depth: number) => void;
  /** 0..1 temporal de-speckle for Pop: suppresses energy that does not persist
   *  into adjacent time rows (random noise breakthrough), keeping continuous
   *  signal traces. 0 = off (raw RX). Driven by the waterfall-smoothness knob. */
  setCleanup: (strength: number) => void;
  /** 0..1 Pop signal-glow: lifts carrier crests into the bright colour band and
   *  adds a cyan-white crest/specular highlight. 0 = off (plain hillshade). */
  setPopGlow: (intensity: number) => void;
  /** Continuous scroll rate (rows/frame); parity with the WebGL knob. */
  setScrollSpeed: (speed: number) => void;
  /** Drop the visible history (writeRow/validRows reset) without reallocating.
   *  Used when the value domain changes wholesale (Pop on/off → dB↔0..1, or
   *  RX↔TX dB window) so pre-toggle rows don't render as a clipped band. */
  clearHistory: () => void;
  debugState: () => { texWidth: number; writeRow: number; validRows: number };
  dispose: () => void;
};

const UNIFORM_FLOATS = 20; // 80 bytes — see WGSL Uniforms struct.

// TS 5.7 types TypedArrays as ArrayBufferLike-backed, but the WebGPU queue wants
// ArrayBuffer-backed views (it rejects SharedArrayBuffer). None of ours are
// SharedArrayBuffer-backed, so narrow at the upload boundary.
function gpuSrc(view: Float32Array | Uint8Array): GPUAllowSharedBufferSource {
  return view as unknown as GPUAllowSharedBufferSource;
}

function lutTextureBytes(id: RenderColormapId): Uint8Array {
  const lut = lutFor(id);
  if (lut.length !== 256 * 4) throw new Error(`colormap LUT must be 1024 bytes, got ${lut.length}`);
  return lut;
}

const SHADER = /* wgsl */ `
struct Uniforms {
  lightDir : vec4<f32>,
  p0 : vec4<f32>,  // writeRow, historyRows, texW, validRows
  p1 : vec4<f32>,  // dbMin, dbMax, reliefScale, reliefDepth
  p2 : vec4<f32>,  // canvasW, visibleRows, scroll, cleanup
  p3 : vec4<f32>,  // viewCentreHz, viewHzPerPixel, popGlow, _
};
@group(0) @binding(0) var<uniform> u : Uniforms;
@group(0) @binding(1) var historyTex : texture_2d<f32>;
@group(0) @binding(2) var lutTex : texture_2d<f32>;
@group(0) @binding(3) var lutSampler : sampler;
@group(0) @binding(4) var<storage, read> rowGeom : array<vec2<f32>>;

fn ringRow(age : f32) -> i32 {
  let h = u.p0.y;
  let r = ((u.p0.x - age) % h + h) % h;
  return i32(r);
}

// Level (0..1) at screen column uvx, "age" rows back. Each row maps its OWN
// native columns (rowGeom centre/span) to the current view window (p3), so a
// zoom scales and a retune slides per row — seamless, no remap pass.
fn levelAt(uvx : f32, age : f32) -> f32 {
  if (age > u.p0.w - 1.0) { return 0.0; }       // older than history → floor
  let row = ringRow(age);
  let g = rowGeom[row];
  if (g.y <= 0.0) { return 0.0; }               // unwritten row → floor
  let texW = u.p0.z;
  let srcX = 0.5 + (u.p3.x - g.x) / (g.y * texW) + (uvx - 0.5) * (u.p3.y / g.y);
  if (srcX < 0.0 || srcX > 1.0) { return 0.0; } // exposed edge → floor
  let texX = clamp(i32(round(srcX * (texW - 1.0))), 0, i32(texW) - 1);
  let db = textureLoad(historyTex, vec2<i32>(texX, row), 0).r;
  return clamp((db - u.p1.x) / max(0.0001, u.p1.y - u.p1.x), 0.0, 1.0);
}

// Temporal de-speckle (Pop). A real signal persists into adjacent captured rows;
// random noise breaking the floor-subtraction gate does not. Gate the current
// level by whether a neighbouring row also carries energy. cleanup (p2.w): 0 =
// off → a single tap (free for normal RX); 1 = full persistence gate.
fn cleanLevel(uvx : f32, age : f32) -> f32 {
  let cur = levelAt(uvx, age);
  let cleanup = u.p2.w;
  if (cleanup <= 0.001) { return cur; }
  let support = max(levelAt(uvx, age + 1.0), levelAt(uvx, max(0.0, age - 1.0)));
  let gate = mix(1.0, smoothstep(0.03, 0.40, support), cleanup);
  return cur * gate;
}

struct VsOut {
  @builtin(position) clip : vec4<f32>,
  @location(0) uv : vec2<f32>,
};

@vertex
fn vs(@builtin(vertex_index) vi : u32) -> VsOut {
  // Single fullscreen triangle — no vertex buffer.
  var corners = array<vec2<f32>, 3>(vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
  let p = corners[vi];
  var out : VsOut;
  out.clip = vec4<f32>(p, 0.0, 1.0);
  // uv.x 0..1 left→right (frequency); uv.y 0 at top (newest) .. 1 bottom (oldest).
  out.uv = vec2<f32>(p.x * 0.5 + 0.5, 0.5 - p.y * 0.5);
  return out;
}

@fragment
fn fs(in : VsOut) -> @location(0) vec4<f32> {
  let scroll = max(0.05, u.p2.z);
  let visibleRows = max(1.0, u.p2.y);
  let canvasW = max(1.0, u.p2.x);
  let reliefScale = u.p1.z;
  let reliefDepth = u.p1.w;
  let popGlow = u.p3.z;

  let age = in.uv.y * visibleRows / scroll;
  let level = cleanLevel(in.uv.x, age);

  // 4-tap hillshade for the topographic relief — neighbours one pixel / one row
  // away give a cheap surface normal, then Lambert. Contrast tuned by reliefDepth.
  // Uses the de-speckled level so relief follows clean signal traces, not noise.
  let dx = 1.0 / canvasW;
  let dAge = 1.0 / scroll;
  let lx = cleanLevel(in.uv.x - dx, age);
  let rx = cleanLevel(in.uv.x + dx, age);
  let ol = cleanLevel(in.uv.x, age + dAge);
  let nw = cleanLevel(in.uv.x, max(0.0, age - dAge));
  let normal = normalize(vec3<f32>((lx - rx) * reliefScale, 1.0, (nw - ol) * reliefScale));
  let lightDir = normalize(u.lightDir.xyz);
  let lambert = clamp(dot(normal, lightDir) * 0.5 + 0.5, 0.0, 1.0);
  let shade = 0.10 + mix(1.55, 2.15, reliefDepth) * pow(lambert, 1.55);

  // Ridge prominence: how far this pixel stands above its 4-neighbour mean. High
  // on carrier tops and signal edges, ~0 on flat noise/background — the basis for
  // the Pop "lift + glow" that makes signals leap off the floor.
  let cross = (lx + rx + ol + nw) * 0.25;
  let crest = max(0.0, level - cross);
  // Sharp specular glint on the lit face of a ridge — sparkle on strong carriers.
  let spec = pow(max(dot(normal, lightDir), 0.0), 22.0);
  // Only let glow touch real signal, never the noise floor.
  let strength = smoothstep(0.18, 0.55, level);

  // Pop lift: push crests into a brighter colour band so they separate from the
  // floor — but only WITHIN the colormap, so the visual-AGC'd level (already
  // scaled to the scene's peaks/floor) still owns where the top is. Gated by
  // popGlow → zero in normal RX (the surface stays the plain hillshade there).
  let lifted = clamp(level + crest * 1.1 * popGlow, 0.0, 1.0);
  let lvl = clamp((lifted - 0.5) * 1.12 + 0.5, 0.0, 1.0);
  let base = textureSample(lutTex, lutSampler, vec2<f32>(lvl, 0.5)).rgb;
  var col = base * shade;
  // Crest sheen + specular glint, cyan-white. Faded by HEADROOM (1 - lvl) so the
  // brightest carriers don't blow past the ramp top — they stay defined and the
  // colormap owns the peak; the glow lives on the rising mid-levels.
  let headroom = 1.0 - lvl;
  let glow = (crest * 1.1 + spec * crest * 1.3) * strength * popGlow * headroom;
  col += vec3<f32>(0.48, 0.74, 1.0) * glow;
  return vec4<f32>(min(col, vec3<f32>(1.0)), 1.0);
}
`;

export function createHeightfieldRenderer(
  device: GPUDevice,
  context: GPUCanvasContext,
  format: GPUTextureFormat,
): HeightfieldRenderer {
  context.configure({ device, format, alphaMode: 'premultiplied' });

  const module = device.createShaderModule({ code: SHADER });
  const uniformBuffer = device.createBuffer({
    size: UNIFORM_FLOATS * 4,
    usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
  });
  const uniformData = new Float32Array(UNIFORM_FLOATS);
  const lutSampler = device.createSampler({ magFilter: 'linear', minFilter: 'linear' });

  const bindGroupLayout = device.createBindGroupLayout({
    entries: [
      { binding: 0, visibility: GPUShaderStage.FRAGMENT, buffer: { type: 'uniform' } },
      { binding: 1, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'unfilterable-float' } },
      { binding: 2, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'float' } },
      { binding: 3, visibility: GPUShaderStage.FRAGMENT, sampler: { type: 'filtering' } },
      { binding: 4, visibility: GPUShaderStage.FRAGMENT, buffer: { type: 'read-only-storage' } },
    ],
  });

  const pipeline = device.createRenderPipeline({
    layout: device.createPipelineLayout({ bindGroupLayouts: [bindGroupLayout] }),
    vertex: { module, entryPoint: 'vs' },
    fragment: { module, entryPoint: 'fs', targets: [{ format }] },
    primitive: { topology: 'triangle-list' },
  });

  const lutTexture = device.createTexture({
    size: [256, 1, 1],
    format: 'rgba8unorm',
    usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST,
  });
  const uploadLut = (id: RenderColormapId) => {
    device.queue.writeTexture(
      { texture: lutTexture },
      gpuSrc(lutTextureBytes(id)),
      { bytesPerRow: 256 * 4, rowsPerImage: 1 },
      { width: 256, height: 1, depthOrArrayLayers: 1 },
    );
  };
  uploadLut('blue');

  // Per-row capture geometry (centreHz, hzPerPixel) — one vec2 per ring slot.
  const rowGeomBuffer = device.createBuffer({
    size: HISTORY_ROWS * 2 * 4,
    usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
  });
  const rowGeomScratch = new Float32Array(2);
  const rowGeomZero = new Float32Array(HISTORY_ROWS * 2);

  let texWidth = 0;
  let writeRow = 0;
  let validRows = 0;
  let historyTexture: GPUTexture | null = null;
  let bindGroup: GPUBindGroup | null = null;
  let canvasW = 1;
  let canvasH = 1;
  let reliefDepth = 0.55;
  let scrollSpeed = 1;
  let cleanup = 0;
  let popGlow = 0;
  let anchorCenterHz: number | null = null;
  let anchorHzPerPixel = 0;

  const rebuildBindGroup = () => {
    if (!historyTexture) return;
    bindGroup = device.createBindGroup({
      layout: bindGroupLayout,
      entries: [
        { binding: 0, resource: { buffer: uniformBuffer } },
        { binding: 1, resource: historyTexture.createView() },
        { binding: 2, resource: lutTexture.createView() },
        { binding: 3, resource: lutSampler },
        { binding: 4, resource: { buffer: rowGeomBuffer } },
      ],
    });
  };

  const allocHistory = (width: number) => {
    historyTexture?.destroy();
    historyTexture = device.createTexture({
      size: [width, HISTORY_ROWS, 1],
      format: 'r32float',
      usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST,
    });
    // No texture seed: WebGPU zero-inits, and unwritten rows are gated to the
    // floor by rowGeom (hzPerPixel 0) + validRows. Only the small rowGeom buffer
    // must be cleared so stale slots from a previous width read as unwritten.
    device.queue.writeBuffer(rowGeomBuffer, 0, gpuSrc(rowGeomZero));
    texWidth = width;
    writeRow = 0;
    validRows = 0;
    rebuildBindGroup();
  };

  const writeUniforms = (dbMin: number, dbMax: number, viewCenterHz: number, viewHzPerPixel: number) => {
    // lightDir: cartographic NW (upper-left) and above; normalized in-shader.
    uniformData[0] = -0.5;
    uniformData[1] = 0.8;
    uniformData[2] = -0.5;
    uniformData[3] = 0;
    // p0: writeRow, historyRows, texW, validRows
    uniformData[4] = writeRow;
    uniformData[5] = HISTORY_ROWS;
    uniformData[6] = texWidth;
    uniformData[7] = validRows;
    // p1: dbMin, dbMax, reliefScale, reliefDepth. Deep relief range (6..32) so
    // signals stand off the noise floor as illuminated ridges — especially with
    // Pop, where floor subtraction flattens the baseline so the relief lights up
    // only real carriers.
    uniformData[8] = dbMin;
    uniformData[9] = dbMax;
    uniformData[10] = 6 + 26 * reliefDepth;
    uniformData[11] = reliefDepth;
    // p2: canvasW, visibleRows (≈ canvasH), scroll, _
    uniformData[12] = canvasW;
    uniformData[13] = canvasH;
    uniformData[14] = scrollSpeed;
    uniformData[15] = cleanup;
    // p3: viewCentreHz, viewHzPerPixel, popGlow, _
    uniformData[16] = viewCenterHz;
    uniformData[17] = viewHzPerPixel;
    uniformData[18] = popGlow;
    uniformData[19] = 0;
    device.queue.writeBuffer(uniformBuffer, 0, gpuSrc(uniformData));
  };

  return {
    pushRow(rowDb, centerHz, hzPerPixel) {
      if (rowDb.length === 0) return;
      if (texWidth !== rowDb.length) allocHistory(rowDb.length);
      if (!historyTexture) return;
      anchorCenterHz = Number.isFinite(centerHz) ? centerHz : anchorCenterHz;
      if (Number.isFinite(hzPerPixel) && hzPerPixel > 0) anchorHzPerPixel = hzPerPixel;
      writeRow = (writeRow + 1) % HISTORY_ROWS;
      device.queue.writeTexture(
        { texture: historyTexture, origin: { x: 0, y: writeRow, z: 0 } },
        gpuSrc(rowDb),
        { bytesPerRow: rowDb.length * 4, rowsPerImage: 1 },
        { width: rowDb.length, height: 1, depthOrArrayLayers: 1 },
      );
      rowGeomScratch[0] = Number.isFinite(centerHz) ? centerHz : 0;
      rowGeomScratch[1] = Number.isFinite(hzPerPixel) && hzPerPixel > 0 ? hzPerPixel : 0;
      device.queue.writeBuffer(rowGeomBuffer, writeRow * 2 * 4, gpuSrc(rowGeomScratch));
      validRows = Math.min(HISTORY_ROWS, validRows + 1);
    },
    draw(dbMin, dbMax, viewCenterHz = null, viewHzPerPixel = null) {
      if (!bindGroup) return;
      const vCenter =
        viewCenterHz !== null && Number.isFinite(viewCenterHz) ? viewCenterHz : anchorCenterHz ?? 0;
      const vHz = viewHzPerPixel !== null && viewHzPerPixel > 0 ? viewHzPerPixel : anchorHzPerPixel;
      writeUniforms(dbMin, dbMax, vCenter, vHz);
      const encoder = device.createCommandEncoder();
      const pass = encoder.beginRenderPass({
        colorAttachments: [
          {
            view: context.getCurrentTexture().createView(),
            clearValue: { r: 0, g: 0, b: 0, a: 0 },
            loadOp: 'clear',
            storeOp: 'store',
          },
        ],
      });
      pass.setPipeline(pipeline);
      pass.setBindGroup(0, bindGroup);
      pass.draw(3);
      pass.end();
      device.queue.submit([encoder.finish()]);
    },
    resize(w, h) {
      canvasW = Math.max(1, Math.floor(w));
      canvasH = Math.max(1, Math.floor(h));
    },
    setColormap(id) {
      uploadLut(id);
    },
    setReliefDepth(depth) {
      reliefDepth = Number.isFinite(depth) ? Math.max(0, Math.min(1, depth)) : reliefDepth;
    },
    setPopGlow(intensity) {
      popGlow = Number.isFinite(intensity) ? Math.max(0, Math.min(1, intensity)) : 0;
    },
    setCleanup(strength) {
      cleanup = Number.isFinite(strength) ? Math.max(0, Math.min(1, strength)) : 0;
    },
    setScrollSpeed(speed) {
      scrollSpeed = Number.isFinite(speed) ? Math.max(0.25, Math.min(2.5, speed)) : 1;
    },
    clearHistory() {
      if (texWidth === 0) return;
      writeRow = 0;
      validRows = 0;
      // Zero rowGeom so every slot reads as unwritten → floor, regardless of the
      // (now stale-domain) texture contents.
      device.queue.writeBuffer(rowGeomBuffer, 0, gpuSrc(rowGeomZero));
    },
    debugState() {
      return { texWidth, writeRow, validRows };
    },
    dispose() {
      historyTexture?.destroy();
      lutTexture.destroy();
      uniformBuffer.destroy();
      rowGeomBuffer.destroy();
    },
  };
}

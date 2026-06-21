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

// R32F history texture sized width × HISTORY_ROWS with a rolling writeRow
// index. Each incoming row is uploaded via texSubImage2D into the next ring
// slot and the fragment shader reads with a rolling vertical offset so the
// newest row always sits at the top.
//
// Doc 08 §5 ping-pong: on VFO change we horizontally shift the existing
// history so carriers at a fixed absolute frequency stay at the same pixel
// column across a retune. Two R32F textures A/B share a reused FBO; the
// shift fragment pass reads from the active texture and writes a shifted
// copy into the inactive one, then swaps. Suppressing the row blit for the
// shift tick avoids a discontinuity between the just-shifted top row and the
// pre-retune frame underneath.
//
// Reset conditions (|shift| ≥ width or width change) re-seed both textures
// at -200 dB so uninitialised columns render as the noise-floor colour rather
// than a 0 dB yellow band. Hz-per-pixel changes are resampled in-place when
// the old and new spans overlap, so zooming preserves waterfall history.

import { buildProgram } from './util';
import { WF_VS, WF_FS, WF_REMAP_FS } from './shaders';
import { lutFor, type RenderColormapId } from './colormap';
import type { WfShiftDecision } from './wf-shift';

const HISTORY_ROWS = 4096;
const SEED_DB = -200;

export type PushOptions = {
  // Skip the per-row texSubImage2D during 'push' decisions. The shift-state
  // tracker and reset/shift paths still run, so VFO retunes keep the history
  // coherent while the Waterfall component throttles row uploads (task #25).
  skipRowUpload?: boolean;
  /** Optional 0..1 terrain/evidence height row aligned with wfDb. */
  terrainRow?: Float32Array | null;
};

/** WebGL float-texture capabilities, surfaced so the component can show an
 *  on-screen badge when a feature needed by the waterfall is missing (#629). */
export type WfGlCaps = {
  floatLinear: boolean;
  colorBufferFloat: boolean;
  gpu: string;
};

export type WfRenderer = {
  /** GL float-texture capabilities of the context this renderer was built on. */
  caps: WfGlCaps;
  resize: (w: number, h: number) => void;
  /** Apply the shared per-frame plan (issue #597: the decision is computed
   *  once in gl/frame-plan.ts and handed to both spectrum surfaces so they
   *  can never disagree). `wfDb` may be null on frames whose waterfall
   *  payload is invalid — geometry (shift/reset) still applies so the
   *  history stays aligned with the panadapter. */
  pushFrame: (
    decision: WfShiftDecision,
    wfDb: Float32Array | null,
    centerHz: bigint,
    hzPerPixel: number,
    options?: PushOptions,
  ) => void;
  /** Draw the history. `viewCenterHz` is the animated view-center; when
   *  non-null the sampling window slides by the fractional offset between
   *  the history's anchor center and the view (issue #597). `viewHzPerPixel`
   *  is the animated display span (view-zoom.ts); when it differs from the
   *  span the history was rebased at, the sampler scales about the view centre
   *  so a zoom glides instead of snapping — no texture resample. Null/omitted
   *  renders at unit scale and zero offset (pre-first-frame / tests). */
  draw: (
    dbMin: number,
    dbMax: number,
    viewCenterHz?: number | null,
    viewHzPerPixel?: number | null,
  ) => void;
  setColormap: (id: RenderColormapId) => void;
  /** Enables and tunes the Signal Pop waterfall shader treatment. */
  setPopMode: (active: boolean, intensity?: number, reliefDepth?: number, smoothness?: number) => void;
  /** Continuous vertical waterfall scroll rate. 1.0 is one display pixel per frame. */
  setScrollSpeed: (speed: number) => void;
  /** 1.0 = opaque (default). 0.0 = noise floor fades to transparent so a
   *  background layer (e.g. the QRZ-mode Leaflet map) shows through. */
  setTransparent: (transparent: boolean) => void;
  /** Runtime render state, posted to the BACKEND log (#629) because the
   *  desktop app has no reachable DevTools — lets us confirm the waterfall
   *  seeded (texWidth > 0) headlessly. */
  debugState: () => {
    texWidth: number;
    writeRow: number;
    validRows: number;
    scrollSpeed: number;
    lastViewOffsetUv: number;
    contextLost: boolean;
  };
  /** Re-seed the history to the noise-floor colour. Used when the value scale
   *  changes wholesale (Signal Pop toggle) so pre-toggle rows in the old dB
   *  domain don't render as a clipped band against the new range. No-op before
   *  the first frame (nothing allocated yet). */
  clearHistory: () => void;
  dispose: () => void;
};

export function createWfRenderer(gl: WebGL2RenderingContext): WfRenderer {
  // R32F as a color attachment requires EXT_color_buffer_float; LINEAR
  // filtering on floats needs OES_texture_float_linear. Both are requested
  // for effect — we don't consume the extension objects directly.
  const colorExt = gl.getExtension('EXT_color_buffer_float');
  const floatExt = gl.getExtension('OES_texture_float_linear');
  // LINEAR filtering on a FLOAT (R32F) texture is ONLY legal when
  // OES_texture_float_linear is present. Without it, sampling the history with
  // gl.LINEAR makes the texture "incomplete" and the fragment shader's
  // texture() reads return garbage — the waterfall renders wrong or blanks
  // entirely while the (non-float) panadapter is unaffected. This is the prime
  // suspect for the Windows/WebView2-in-a-VM case in #629: ANGLE on a software
  // or weak GPU frequently omits this extension. Fall back to NEAREST so the
  // waterfall still renders — we lose only sub-pixel-smooth horizontal glide,
  // not the data.
  const histMagFilter = floatExt ? gl.LINEAR : gl.NEAREST;
  const dbg = gl.getExtension('WEBGL_debug_renderer_info');
  const caps: WfGlCaps = {
    floatLinear: !!floatExt,
    colorBufferFloat: !!colorExt,
    gpu: dbg ? String(gl.getParameter(dbg.UNMASKED_RENDERER_WEBGL)) : 'unknown',
  };
  // One-time capability log — surfaces in any console; the Waterfall component
  // also renders an on-screen badge when floatLinear is false, because the
  // desktop (WebView2) app has no reachable DevTools (#629).

  console.info(
    `[waterfall] gl caps: float_linear=${caps.floatLinear} ` +
      `color_buffer_float=${caps.colorBufferFloat} ` +
      `magFilter=${floatExt ? 'LINEAR' : 'NEAREST(fallback)'} gpu=${caps.gpu}`,
  );

  const drawProg = buildProgram(gl, WF_VS, WF_FS);
  const uHistory = gl.getUniformLocation(drawProg, 'uHistory');
  const uTerrainHistory = gl.getUniformLocation(drawProg, 'uTerrainHistory');
  const uLut = gl.getUniformLocation(drawProg, 'uLut');
  const uDbMin = gl.getUniformLocation(drawProg, 'uDbMin');
  const uDbMax = gl.getUniformLocation(drawProg, 'uDbMax');
  const uWriteRow = gl.getUniformLocation(drawProg, 'uWriteRow');
  const uH = gl.getUniformLocation(drawProg, 'uH');
  const uVisibleRows = gl.getUniformLocation(drawProg, 'uVisibleRows');
  const uScrollSpeed = gl.getUniformLocation(drawProg, 'uScrollSpeed');
  const uValidRows = gl.getUniformLocation(drawProg, 'uValidRows');
  const uBgAlpha = gl.getUniformLocation(drawProg, 'uBgAlpha');
  const uViewOffsetUv = gl.getUniformLocation(drawProg, 'uViewOffsetUv');
  const uViewScale = gl.getUniformLocation(drawProg, 'uViewScale');
  const uSeedDbDraw = gl.getUniformLocation(drawProg, 'uSeedDb');
  const uPopActive = gl.getUniformLocation(drawProg, 'uPopActive');
  const uPopIntensity = gl.getUniformLocation(drawProg, 'uPopIntensity');
  const uReliefDepth = gl.getUniformLocation(drawProg, 'uReliefDepth');
  const uSmoothness = gl.getUniformLocation(drawProg, 'uSmoothness');
  const uTexW = gl.getUniformLocation(drawProg, 'uTexW');
  const uCanvasW = gl.getUniformLocation(drawProg, 'uCanvasW');
  let bgAlpha = 1;
  let popActive = false;
  let popIntensity = 0;
  let reliefDepth = 0;
  let smoothness = 0;
  let scrollSpeed = 1;

  const remapProg = buildProgram(gl, WF_VS, WF_REMAP_FS);
  const uRemapSrc = gl.getUniformLocation(remapProg, 'uSrc');
  const uRemapSrcXScale = gl.getUniformLocation(remapProg, 'uSrcXScale');
  const uRemapSrcCenterOffsetUv = gl.getUniformLocation(remapProg, 'uSrcCenterOffsetUv');
  const uRemapSeed = gl.getUniformLocation(remapProg, 'uSeedDb');

  const vao = gl.createVertexArray()!;
  const vbo = gl.createBuffer()!;
  gl.bindVertexArray(vao);
  gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
  gl.bufferData(
    gl.ARRAY_BUFFER,
    new Float32Array([-1, -1, 3, -1, -1, 3]),
    gl.STATIC_DRAW,
  );
  gl.enableVertexAttribArray(0);
  gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
  gl.bindVertexArray(null);

  const textures: [WebGLTexture, WebGLTexture] = [
    gl.createTexture()!,
    gl.createTexture()!,
  ];
  const terrainTextures: [WebGLTexture, WebGLTexture] = [
    gl.createTexture()!,
    gl.createTexture()!,
  ];
  const initTextureParams = (tex: WebGLTexture) => {
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, histMagFilter);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  };
  initTextureParams(textures[0]);
  initTextureParams(textures[1]);
  initTextureParams(terrainTextures[0]);
  initTextureParams(terrainTextures[1]);

  const fbo = gl.createFramebuffer()!;

  const lutTex = gl.createTexture()!;
  const uploadLut = (id: RenderColormapId) => {
    gl.bindTexture(gl.TEXTURE_2D, lutTex);
    gl.texImage2D(
      gl.TEXTURE_2D,
      0,
      gl.RGBA8,
      256,
      1,
      0,
      gl.RGBA,
      gl.UNSIGNED_BYTE,
      lutFor(id),
    );
  };
  uploadLut('blue');
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);

  let texWidth = 0;
  let writeRow = 0;
  let active: 0 | 1 = 0;
  let lastCenterHz: bigint | null = null;
  let lastHzPerPixel = 0;
  let canvasW = 0;
  let canvasH = 0;
  // Last width the history textures were seeded at — lets a 'reset' decision on
  // a null-wf frame re-seed at the known column count (#629 hardening).
  let lastValidWidth = 0;
  // Last view offset applied at draw — surfaced via debugState for #629 triage.
  let lastViewOffsetUv = 0;
  // Rows uploaded since the last hard seed/remap that exposed new territory.
  // Slow scroll rates can otherwise sample thousands of rows deep and reveal
  // old seeded rectangles before live data has filled them.
  let validRows = 0;
  let zeroTerrainRow: Float32Array | null = null;

  const seedTexture = (tex: WebGLTexture, w: number, seedValue: number) => {
    gl.bindTexture(gl.TEXTURE_2D, tex);
    const seed = new Float32Array(w * HISTORY_ROWS);
    seed.fill(seedValue);
    gl.texImage2D(
      gl.TEXTURE_2D,
      0,
      gl.R32F,
      w,
      HISTORY_ROWS,
      0,
      gl.RED,
      gl.FLOAT,
      seed,
    );
  };

  const resetTextures = (w: number) => {
    seedTexture(textures[0], w, SEED_DB);
    seedTexture(textures[1], w, SEED_DB);
    seedTexture(terrainTextures[0], w, 0);
    seedTexture(terrainTextures[1], w, 0);
    texWidth = w;
    lastValidWidth = w;
    writeRow = 0;
    validRows = 0;
    active = 0;
    zeroTerrainRow = null;
  };

  const terrainFallbackRow = (w: number): Float32Array => {
    if (zeroTerrainRow === null || zeroTerrainRow.length !== w) zeroTerrainRow = new Float32Array(w);
    else zeroTerrainRow.fill(0);
    return zeroTerrainRow;
  };

  const uploadRow = (wfDb: Float32Array, terrainRow?: Float32Array | null) => {
    // Lazy seed (#629): the first valid waterfall row seeds the history,
    // regardless of whether a 'reset' decision happened to carry wf data.
    // Before this, seeding only ran inside the 'reset' branch when that exact
    // frame had a non-null wfDb; if the first post-connect frame's wf payload
    // was null (timing-dependent — reliably so on Windows), the textures never
    // got R32F storage, texWidth stayed 0, draw() bailed to a transparent
    // clear, and the waterfall was permanently black. Seeding here (and on any
    // width change) makes it order- and platform-independent.
    if (wfDb.length === 0) return;
    if (texWidth !== wfDb.length) resetTextures(wfDb.length);
    writeRow = (writeRow + 1) % HISTORY_ROWS;
    gl.bindTexture(gl.TEXTURE_2D, textures[active]);
    gl.texSubImage2D(
      gl.TEXTURE_2D,
      0,
      0,
      writeRow,
      wfDb.length,
      1,
      gl.RED,
      gl.FLOAT,
      wfDb,
    );
    const terrain = terrainRow && terrainRow.length === wfDb.length
      ? terrainRow
      : terrainFallbackRow(wfDb.length);
    gl.bindTexture(gl.TEXTURE_2D, terrainTextures[active]);
    gl.texSubImage2D(
      gl.TEXTURE_2D,
      0,
      0,
      writeRow,
      wfDb.length,
      1,
      gl.RED,
      gl.FLOAT,
      terrain,
    );
    validRows = Math.min(HISTORY_ROWS, validRows + 1);
  };

  const capVisibleHistory = (rows = 0) => {
    validRows = Math.min(validRows, Math.max(0, Math.min(HISTORY_ROWS, rows)));
  };

  const performRemapTexture = (
    pair: [WebGLTexture, WebGLTexture],
    srcCenterOffsetUv: number,
    srcXScale: number,
    seedValue: number,
  ) => {
    const src = active === 0 ? pair[0] : pair[1];
    const dst = active === 0 ? pair[1] : pair[0];
    gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
    gl.framebufferTexture2D(
      gl.FRAMEBUFFER,
      gl.COLOR_ATTACHMENT0,
      gl.TEXTURE_2D,
      dst,
      0,
    );
    gl.viewport(0, 0, texWidth, HISTORY_ROWS);
    gl.useProgram(remapProg);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, src);
    gl.uniform1i(uRemapSrc, 0);
    gl.uniform1f(uRemapSrcXScale, srcXScale);
    gl.uniform1f(uRemapSrcCenterOffsetUv, srcCenterOffsetUv);
    gl.uniform1f(uRemapSeed, seedValue);
    gl.bindVertexArray(vao);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    gl.bindVertexArray(null);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
  };

  const performRemap = (srcCenterOffsetUv: number, srcXScale: number) => {
    if (texWidth === 0) return; // not seeded yet — nothing to remap
    performRemapTexture(textures, srcCenterOffsetUv, srcXScale, SEED_DB);
    performRemapTexture(terrainTextures, srcCenterOffsetUv, srcXScale, 0);
    active = (1 - active) as 0 | 1;
  };

  const performShift = (shiftPx: number) => {
    if (texWidth === 0) return;
    performRemap(-shiftPx / texWidth, 1);
    if (Math.abs(shiftPx) > Math.max(8, texWidth * 0.015)) capVisibleHistory();
  };

  return {
    caps,
    resize(w, h) {
      canvasW = w;
      canvasH = h;
      gl.viewport(0, 0, w, h);
    },
    pushFrame(decision, wfDb, centerHz, hzPerPixel, options) {
      switch (decision.kind) {
        case 'reset': {
          // Reset re-seeds both textures; when this frame carries valid wf
          // data also upload it so the history has a real top row. A reset
          // on an invalid-wf frame leaves the seed only — the next valid
          // frame pushes the first real row.
          const width = wfDb?.length ?? (texWidth || lastValidWidth);
          if (width > 0) resetTextures(width);
          if (wfDb) uploadRow(wfDb, options?.terrainRow);
          lastCenterHz = centerHz;
          lastHzPerPixel = hzPerPixel;
          break;
        }
        case 'push':
          if (wfDb && !options?.skipRowUpload) uploadRow(wfDb, options?.terrainRow);
          // lastCenterHz unchanged so sub-pixel retunes accumulate.
          break;
        case 'shift':
          // A shift on an unseeded renderer (texWidth === 0, e.g. the first
          // frame this surface observes after a remount happens to be a
          // retune) has no history to rebase and would bind a level-0 store
          // that texImage2D never allocated. Skip it; the next valid frame
          // seeds via uploadRow.
          if (texWidth === 0) break;
          // Shift always runs — throttling it would let the history drift
          // out of sync with the panadapter's anchor offset. This is a
          // REBASE of the texture in integer pixels; the fractional
          // remainder is rendered at draw time from the view-center.
          performShift(decision.shiftPx);
          // Suppress the new-row blit this tick per doc 08 §5 so we don't
          // overlay a post-retune row on top of a just-shifted frame.
          lastCenterHz = decision.residualCenterHz;
          break;
        case 'rescale':
          // Remap each historical column to the equivalent absolute-frequency
          // column under the new hz/px. This keeps zoom changes continuous:
          // zoom-in crops around the center; zoom-out exposes seeded edges.
          performRemap(decision.srcCenterOffsetUv, decision.srcXScale);
          if (Math.abs(decision.srcCenterOffsetUv) + decision.srcXScale * 0.5 > 0.5) {
            capVisibleHistory();
          }
          if (wfDb && !options?.skipRowUpload) uploadRow(wfDb, options?.terrainRow);
          lastCenterHz = centerHz;
          lastHzPerPixel = hzPerPixel;
          break;
      }
    },
    setColormap(id) {
      uploadLut(id);
    },
    setTransparent(transparent) {
      bgAlpha = transparent ? 0 : 1;
    },
    setPopMode(active, intensity = active ? 1 : 0, nextReliefDepth = 0, nextSmoothness = 0) {
      popActive = active;
      popIntensity = Number.isFinite(intensity) ? Math.max(0, Math.min(1, intensity)) : 0;
      reliefDepth = Number.isFinite(nextReliefDepth) ? Math.max(0, Math.min(1, nextReliefDepth)) : 0;
      smoothness = Number.isFinite(nextSmoothness) ? Math.max(0, Math.min(1, nextSmoothness)) : 0;
    },
    setScrollSpeed(speed) {
      scrollSpeed = Number.isFinite(speed) ? Math.max(0.25, Math.min(2.5, speed)) : 1;
    },
    clearHistory() {
      if (texWidth > 0) resetTextures(texWidth);
    },
    draw(dbMin, dbMax, viewCenterHz = null, viewHzPerPixel = null) {
      gl.viewport(0, 0, canvasW, canvasH);
      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT);
      if (texWidth === 0) return;
      // Fractional glide between integer rebases (issue #597). Number()
      // on the bigint anchor is exact to 2^53 — fine for 0..60 MHz.
      let viewOffsetUv = 0;
      if (viewCenterHz !== null && lastCenterHz !== null && lastHzPerPixel > 0) {
        const offsetHz = Number(lastCenterHz) - viewCenterHz;
        viewOffsetUv = offsetHz / (lastHzPerPixel * texWidth);
        // A whole-span offset means the view ran away from the history
        // (mid-glide band jump); clamp so sampling math stays sane — the
        // shader's seed fallback paints the exposed region anyway.
        if (!Number.isFinite(viewOffsetUv)) viewOffsetUv = 0;
        viewOffsetUv = Math.max(-1, Math.min(1, viewOffsetUv));
      }
      lastViewOffsetUv = viewOffsetUv;
      // Draw-time zoom (view-zoom.ts): scale the sampled column about the view
      // centre when the animated display span differs from the span the
      // history is rebased at. 1.0 = no zoom. Pure sampling transform — the
      // texture is rebased to the server span once per step elsewhere.
      let viewScale = 1;
      if (viewHzPerPixel !== null && viewHzPerPixel > 0 && lastHzPerPixel > 0) {
        viewScale = viewHzPerPixel / lastHzPerPixel;
        if (!Number.isFinite(viewScale) || viewScale <= 0) viewScale = 1;
      }
      // Premultiplied-alpha blending — matches the fragment output so the
      // noise floor fades cleanly into whatever is behind the canvas.
      gl.enable(gl.BLEND);
      gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
      gl.useProgram(drawProg);
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, textures[active]);
      gl.uniform1i(uHistory, 0);
      gl.activeTexture(gl.TEXTURE1);
      gl.bindTexture(gl.TEXTURE_2D, lutTex);
      gl.uniform1i(uLut, 1);
      gl.activeTexture(gl.TEXTURE2);
      gl.bindTexture(gl.TEXTURE_2D, terrainTextures[active]);
      gl.uniform1i(uTerrainHistory, 2);
      gl.uniform1f(uDbMin, dbMin);
      gl.uniform1f(uDbMax, dbMax);
      gl.uniform1f(uWriteRow, writeRow);
      gl.uniform1f(uH, HISTORY_ROWS);
      gl.uniform1f(uVisibleRows, Math.max(1, canvasH));
      gl.uniform1f(uScrollSpeed, scrollSpeed);
      gl.uniform1f(uValidRows, validRows);
      gl.uniform1f(uBgAlpha, bgAlpha);
      gl.uniform1f(uViewOffsetUv, viewOffsetUv);
      gl.uniform1f(uViewScale, viewScale);
      gl.uniform1f(uSeedDbDraw, SEED_DB);
      gl.uniform1f(uPopActive, popActive ? 1 : 0);
      gl.uniform1f(uPopIntensity, popIntensity);
      gl.uniform1f(uReliefDepth, reliefDepth);
      gl.uniform1f(uSmoothness, smoothness);
      gl.uniform1f(uTexW, texWidth);
      gl.uniform1f(uCanvasW, Math.max(1, canvasW));
      gl.bindVertexArray(vao);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
      gl.bindVertexArray(null);
      gl.disable(gl.BLEND);
    },
    debugState() {
      return {
        texWidth,
        writeRow,
        validRows,
        scrollSpeed,
        lastViewOffsetUv,
        contextLost: gl.isContextLost(),
      };
    },
    dispose() {
      gl.deleteTexture(textures[0]);
      gl.deleteTexture(textures[1]);
      gl.deleteTexture(terrainTextures[0]);
      gl.deleteTexture(terrainTextures[1]);
      gl.deleteTexture(lutTex);
      gl.deleteFramebuffer(fbo);
      gl.deleteBuffer(vbo);
      gl.deleteVertexArray(vao);
      gl.deleteProgram(drawProg);
      gl.deleteProgram(remapProg);
    },
  };
}

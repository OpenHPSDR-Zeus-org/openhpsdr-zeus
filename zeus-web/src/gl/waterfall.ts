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

const HISTORY_ROWS = 512;
const SEED_DB = -200;

export type PushOptions = {
  // Skip the per-row texSubImage2D during 'push' decisions. The shift-state
  // tracker and reset/shift paths still run, so VFO retunes keep the history
  // coherent while the Waterfall component throttles row uploads (task #25).
  skipRowUpload?: boolean;
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
   *  the history's anchor center and the view (issue #597). Null renders
   *  with zero offset (pre-first-frame / tests). */
  draw: (dbMin: number, dbMax: number, viewCenterHz?: number | null) => void;
  setColormap: (id: RenderColormapId) => void;
  /** Enables and tunes the Signal Pop waterfall shader treatment. */
  setPopMode: (active: boolean, intensity?: number) => void;
  /** 1.0 = opaque (default). 0.0 = noise floor fades to transparent so a
   *  background layer (e.g. the QRZ-mode Leaflet map) shows through. */
  setTransparent: (transparent: boolean) => void;
  /** Runtime render state, posted to the BACKEND log (#629) because the
   *  desktop app has no reachable DevTools — lets us confirm the waterfall
   *  seeded (texWidth > 0) headlessly. */
  debugState: () => {
    texWidth: number;
    writeRow: number;
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
  // eslint-disable-next-line no-console
  console.info(
    `[waterfall] gl caps: float_linear=${caps.floatLinear} ` +
      `color_buffer_float=${caps.colorBufferFloat} ` +
      `magFilter=${floatExt ? 'LINEAR' : 'NEAREST(fallback)'} gpu=${caps.gpu}`,
  );

  const drawProg = buildProgram(gl, WF_VS, WF_FS);
  const uHistory = gl.getUniformLocation(drawProg, 'uHistory');
  const uLut = gl.getUniformLocation(drawProg, 'uLut');
  const uDbMin = gl.getUniformLocation(drawProg, 'uDbMin');
  const uDbMax = gl.getUniformLocation(drawProg, 'uDbMax');
  const uWriteRow = gl.getUniformLocation(drawProg, 'uWriteRow');
  const uH = gl.getUniformLocation(drawProg, 'uH');
  const uBgAlpha = gl.getUniformLocation(drawProg, 'uBgAlpha');
  const uViewOffsetUv = gl.getUniformLocation(drawProg, 'uViewOffsetUv');
  const uSeedDbDraw = gl.getUniformLocation(drawProg, 'uSeedDb');
  const uPopActive = gl.getUniformLocation(drawProg, 'uPopActive');
  const uPopIntensity = gl.getUniformLocation(drawProg, 'uPopIntensity');
  let bgAlpha = 1;
  let popActive = false;
  let popIntensity = 0;

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
  const initTextureParams = (tex: WebGLTexture) => {
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, histMagFilter);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  };
  initTextureParams(textures[0]);
  initTextureParams(textures[1]);

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

  const seedTexture = (tex: WebGLTexture, w: number) => {
    gl.bindTexture(gl.TEXTURE_2D, tex);
    const seed = new Float32Array(w * HISTORY_ROWS);
    seed.fill(SEED_DB);
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
    seedTexture(textures[0], w);
    seedTexture(textures[1], w);
    texWidth = w;
    lastValidWidth = w;
    writeRow = 0;
    active = 0;
  };

  const uploadRow = (wfDb: Float32Array) => {
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
  };

  const performRemap = (srcCenterOffsetUv: number, srcXScale: number) => {
    if (texWidth === 0) return; // not seeded yet — nothing to remap
    const src = active === 0 ? textures[0] : textures[1];
    const dst = active === 0 ? textures[1] : textures[0];
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
    gl.uniform1f(uRemapSeed, SEED_DB);
    gl.bindVertexArray(vao);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    gl.bindVertexArray(null);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    active = (1 - active) as 0 | 1;
  };

  const performShift = (shiftPx: number) => {
    if (texWidth === 0) return;
    performRemap(-shiftPx / texWidth, 1);
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
          if (wfDb) uploadRow(wfDb);
          lastCenterHz = centerHz;
          lastHzPerPixel = hzPerPixel;
          break;
        }
        case 'push':
          if (wfDb && !options?.skipRowUpload) uploadRow(wfDb);
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
          if (wfDb && !options?.skipRowUpload) uploadRow(wfDb);
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
    setPopMode(active, intensity = active ? 1 : 0) {
      popActive = active;
      popIntensity = Number.isFinite(intensity) ? Math.max(0, Math.min(1, intensity)) : 0;
    },
    clearHistory() {
      if (texWidth > 0) resetTextures(texWidth);
    },
    draw(dbMin, dbMax, viewCenterHz = null) {
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
      gl.uniform1f(uDbMin, dbMin);
      gl.uniform1f(uDbMax, dbMax);
      gl.uniform1f(uWriteRow, writeRow);
      gl.uniform1f(uH, HISTORY_ROWS);
      gl.uniform1f(uBgAlpha, bgAlpha);
      gl.uniform1f(uViewOffsetUv, viewOffsetUv);
      gl.uniform1f(uSeedDbDraw, SEED_DB);
      gl.uniform1f(uPopActive, popActive ? 1 : 0);
      gl.uniform1f(uPopIntensity, popIntensity);
      gl.bindVertexArray(vao);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
      gl.bindVertexArray(null);
      gl.disable(gl.BLEND);
    },
    debugState() {
      return {
        texWidth,
        writeRow,
        lastViewOffsetUv,
        contextLost: gl.isContextLost(),
      };
    },
    dispose() {
      gl.deleteTexture(textures[0]);
      gl.deleteTexture(textures[1]);
      gl.deleteTexture(lutTex);
      gl.deleteFramebuffer(fbo);
      gl.deleteBuffer(vbo);
      gl.deleteVertexArray(vao);
      gl.deleteProgram(drawProg);
      gl.deleteProgram(remapProg);
    },
  };
}

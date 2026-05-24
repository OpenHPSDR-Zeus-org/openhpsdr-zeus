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
// Reset conditions (|shift| ≥ width, width change, hzPerPixel change) re-
// seed both textures at -200 dB so uninitialised columns render as the
// noise-floor colour rather than a 0 dB yellow band.

import { buildProgram } from './util';
import { WF_VS, WF_FS, WF_SHIFT_FS } from './shaders';
import { lutFor, type ColormapId } from './colormap';
import { planWaterfallUpdate } from './wf-shift';

const HISTORY_ROWS = 512;
const SEED_DB = -200;

export type PushOptions = {
  // Skip the per-row texSubImage2D during 'push' decisions. The shift-state
  // tracker and reset/shift paths still run, so VFO retunes keep the history
  // coherent while the Waterfall component throttles row uploads (task #25).
  skipRowUpload?: boolean;
};

export type WfRenderer = {
  resize: (w: number, h: number) => void;
  pushFrame: (
    wfDb: Float32Array,
    centerHz: bigint,
    hzPerPixel: number,
    options?: PushOptions,
  ) => void;
  draw: (dbMin: number, dbMax: number) => void;
  setColormap: (id: ColormapId) => void;
  /** 1.0 = opaque (default). 0.0 = noise floor fades to transparent so a
   *  background layer (e.g. the QRZ-mode Leaflet map) shows through. */
  setTransparent: (transparent: boolean) => void;
  dispose: () => void;
};

export function createWfRenderer(gl: WebGL2RenderingContext): WfRenderer {
  // R32F as a color attachment requires EXT_color_buffer_float; LINEAR
  // filtering on floats needs OES_texture_float_linear. Both are requested
  // for effect — we don't consume the extension objects directly.
  const colorExt = gl.getExtension('EXT_color_buffer_float');
  const floatExt = gl.getExtension('OES_texture_float_linear');
  void colorExt;
  void floatExt;

  const drawProg = buildProgram(gl, WF_VS, WF_FS);
  const uHistory = gl.getUniformLocation(drawProg, 'uHistory');
  const uLut = gl.getUniformLocation(drawProg, 'uLut');
  const uDbMin = gl.getUniformLocation(drawProg, 'uDbMin');
  const uDbMax = gl.getUniformLocation(drawProg, 'uDbMax');
  const uWriteRow = gl.getUniformLocation(drawProg, 'uWriteRow');
  const uH = gl.getUniformLocation(drawProg, 'uH');
  const uBgAlpha = gl.getUniformLocation(drawProg, 'uBgAlpha');
  let bgAlpha = 1;

  const shiftProg = buildProgram(gl, WF_VS, WF_SHIFT_FS);
  const uShiftSrc = gl.getUniformLocation(shiftProg, 'uSrc');
  const uShiftUv = gl.getUniformLocation(shiftProg, 'uShiftUv');
  const uShiftSeed = gl.getUniformLocation(shiftProg, 'uSeedDb');

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
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  };
  initTextureParams(textures[0]);
  initTextureParams(textures[1]);

  const fbo = gl.createFramebuffer()!;

  const lutTex = gl.createTexture()!;
  const uploadLut = (id: ColormapId) => {
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
    writeRow = 0;
    active = 0;
  };

  const uploadRow = (wfDb: Float32Array) => {
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

  const performShift = (shiftPx: number) => {
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
    gl.useProgram(shiftProg);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, src);
    gl.uniform1i(uShiftSrc, 0);
    gl.uniform1f(uShiftUv, shiftPx / texWidth);
    gl.uniform1f(uShiftSeed, SEED_DB);
    gl.bindVertexArray(vao);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    gl.bindVertexArray(null);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    active = (1 - active) as 0 | 1;
  };

  return {
    resize(w, h) {
      canvasW = w;
      canvasH = h;
      gl.viewport(0, 0, w, h);
    },
    pushFrame(wfDb, centerHz, hzPerPixel, options) {
      const width = wfDb.length;
      const decision = planWaterfallUpdate({
        lastCenterHz,
        lastHzPerPixel,
        lastWidth: texWidth,
        nextCenterHz: centerHz,
        nextHzPerPixel: hzPerPixel,
        nextWidth: width,
      });
      switch (decision.kind) {
        case 'reset':
          // Reset must always upload so the freshly-seeded history has a
          // real top row; skipping it would leave the whole texture at -200.
          resetTextures(width);
          uploadRow(wfDb);
          lastCenterHz = centerHz;
          lastHzPerPixel = hzPerPixel;
          break;
        case 'push':
          if (!options?.skipRowUpload) uploadRow(wfDb);
          // lastCenterHz unchanged so sub-pixel retunes accumulate.
          break;
        case 'shift':
          // Shift always runs — throttling it would let the history drift
          // out of sync with the panadapter's VFO-accumulated offset.
          performShift(decision.shiftPx);
          // Suppress the new-row blit this tick per doc 08 §5 so we don't
          // overlay a post-retune row on top of a just-shifted frame.
          lastCenterHz = decision.residualCenterHz;
          break;
      }
    },
    setColormap(id) {
      uploadLut(id);
    },
    setTransparent(transparent) {
      bgAlpha = transparent ? 0 : 1;
    },
    draw(dbMin, dbMax) {
      gl.viewport(0, 0, canvasW, canvasH);
      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT);
      if (texWidth === 0) return;
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
      gl.bindVertexArray(vao);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
      gl.bindVertexArray(null);
      gl.disable(gl.BLEND);
    },
    dispose() {
      gl.deleteTexture(textures[0]);
      gl.deleteTexture(textures[1]);
      gl.deleteTexture(lutTex);
      gl.deleteFramebuffer(fbo);
      gl.deleteBuffer(vbo);
      gl.deleteVertexArray(vao);
      gl.deleteProgram(drawProg);
      gl.deleteProgram(shiftProg);
    },
  };
}

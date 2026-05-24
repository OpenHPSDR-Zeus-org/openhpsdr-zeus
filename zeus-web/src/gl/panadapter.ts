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

import { buildProgram } from './util';
import {
  PAN_VS,
  PAN_FS,
  PAN_FILL_VS,
  PAN_FILL_FS,
  CURSOR_VS,
  CURSOR_FS,
} from './shaders';

export type PanRenderer = {
  resize: (w: number, h: number) => void;
  draw: (
    panDb: Float32Array,
    dbMin: number,
    dbMax: number,
    offsetPx: number,
  ) => void;
  // Update the trace + fill colour. Components 0..1, premultiplied alpha is
  // applied inside draw via the FILL_ALPHA_TOP uniform; callers pass plain
  // RGB. No GL re-init — the next draw picks up the new uniform values.
  setTraceColor: (r: number, g: number, b: number) => void;
  dispose: () => void;
};

// Convert a #RRGGBB string into the 0..1 RGB triplet the renderer wants.
// Malformed input falls back to amber so a typo can never crash GL.
export function hexToRgbFloats(hex: string): { r: number; g: number; b: number } {
  const m = /^#([0-9A-Fa-f]{2})([0-9A-Fa-f]{2})([0-9A-Fa-f]{2})$/.exec(hex);
  if (!m || m[1] === undefined || m[2] === undefined || m[3] === undefined) {
    return { r: 1.0, g: 0.627, b: 0.157 };
  }
  return {
    r: parseInt(m[1], 16) / 255,
    g: parseInt(m[2], 16) / 255,
    b: parseInt(m[3], 16) / 255,
  };
}

// Fade at the trace edge; fragment alpha drops to 0 at the floor.
const FILL_ALPHA_TOP = 0.55;

export function createPanRenderer(gl: WebGL2RenderingContext): PanRenderer {
  // Mutable trace colour, default amber. setTraceColor swaps these in place;
  // every draw reads the current values into the fill + line uniforms so the
  // operator's choice from the Display tab takes effect on the next frame.
  let traceR = 1.0;
  let traceG = 0.627;
  let traceB = 0.157;

  const traceProg = buildProgram(gl, PAN_VS, PAN_FS);
  const uTraceWidth = gl.getUniformLocation(traceProg, 'uWidth');
  const uTraceDbMin = gl.getUniformLocation(traceProg, 'uDbMin');
  const uTraceDbMax = gl.getUniformLocation(traceProg, 'uDbMax');
  const uTraceOffsetPx = gl.getUniformLocation(traceProg, 'uOffsetPx');
  const uTraceColor = gl.getUniformLocation(traceProg, 'uColor');

  const fillProg = buildProgram(gl, PAN_FILL_VS, PAN_FILL_FS);
  const uFillWidth = gl.getUniformLocation(fillProg, 'uWidth');
  const uFillDbMin = gl.getUniformLocation(fillProg, 'uDbMin');
  const uFillDbMax = gl.getUniformLocation(fillProg, 'uDbMax');
  const uFillOffsetPx = gl.getUniformLocation(fillProg, 'uOffsetPx');
  const uFillColor = gl.getUniformLocation(fillProg, 'uColor');
  const uFillAlphaTop = gl.getUniformLocation(fillProg, 'uFillAlphaTop');
  const uFillPan = gl.getUniformLocation(fillProg, 'uPan');

  // Trace VBO: one float per bin, rendered as LINE_STRIP for the sharp
  // top edge. Fill reuses the same data via a 1-row R32F texture sampled
  // with `texelFetch(uPan, ivec2(gl_VertexID >> 1, 0))` so both verts of a
  // bin share one dB value without a CPU-side duplication pass.
  const traceVao = gl.createVertexArray()!;
  const traceVbo = gl.createBuffer()!;
  gl.bindVertexArray(traceVao);
  gl.bindBuffer(gl.ARRAY_BUFFER, traceVbo);
  let traceCapacity = 0;
  gl.enableVertexAttribArray(0);
  gl.vertexAttribPointer(0, 1, gl.FLOAT, false, 0, 0);
  gl.bindVertexArray(null);

  // Attribute-less VAO for the fill draw; the shader derives position from
  // gl_VertexID and fetches dB from uPan.
  const fillVao = gl.createVertexArray()!;

  const panTex = gl.createTexture()!;
  gl.bindTexture(gl.TEXTURE_2D, panTex);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  let panTexWidth = 0;

  const cursorProg = buildProgram(gl, CURSOR_VS, CURSOR_FS);
  const uCursorColor = gl.getUniformLocation(cursorProg, 'uColor');
  const cursorVao = gl.createVertexArray()!;
  const cursorVbo = gl.createBuffer()!;
  gl.bindVertexArray(cursorVao);
  gl.bindBuffer(gl.ARRAY_BUFFER, cursorVbo);
  gl.bufferData(
    gl.ARRAY_BUFFER,
    new Float32Array([0, -1, 0, 1]),
    gl.STATIC_DRAW,
  );
  gl.enableVertexAttribArray(0);
  gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
  gl.bindVertexArray(null);

  return {
    resize(w, h) {
      gl.viewport(0, 0, w, h);
    },
    draw(panDb, dbMin, dbMax, offsetPx) {
      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT);

      // Upload pan dB into the 1-row R32F texture. texImage2D re-allocates on
      // width change; texSubImage2D otherwise just streams the row.
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, panTex);
      if (panDb.length !== panTexWidth) {
        gl.texImage2D(
          gl.TEXTURE_2D,
          0,
          gl.R32F,
          panDb.length,
          1,
          0,
          gl.RED,
          gl.FLOAT,
          panDb,
        );
        panTexWidth = panDb.length;
      } else {
        gl.texSubImage2D(
          gl.TEXTURE_2D,
          0,
          0,
          0,
          panDb.length,
          1,
          gl.RED,
          gl.FLOAT,
          panDb,
        );
      }

      // Fill (premultiplied alpha to avoid halo on the glowing top edge).
      gl.enable(gl.BLEND);
      gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
      gl.useProgram(fillProg);
      gl.bindVertexArray(fillVao);
      gl.uniform1i(uFillPan, 0);
      gl.uniform1f(uFillWidth, panDb.length);
      gl.uniform1f(uFillDbMin, dbMin);
      gl.uniform1f(uFillDbMax, dbMax);
      gl.uniform1f(uFillOffsetPx, offsetPx);
      gl.uniform3f(uFillColor, traceR, traceG, traceB);
      gl.uniform1f(uFillAlphaTop, FILL_ALPHA_TOP);
      gl.drawArrays(gl.TRIANGLE_STRIP, 0, panDb.length * 2);

      // Sharp trace line on top.
      gl.disable(gl.BLEND);
      gl.useProgram(traceProg);
      gl.bindVertexArray(traceVao);
      gl.bindBuffer(gl.ARRAY_BUFFER, traceVbo);
      const traceBytes = panDb.byteLength;
      if (traceBytes > traceCapacity) {
        gl.bufferData(gl.ARRAY_BUFFER, traceBytes, gl.STREAM_DRAW);
        traceCapacity = traceBytes;
      }
      gl.bufferSubData(gl.ARRAY_BUFFER, 0, panDb);
      gl.uniform1f(uTraceWidth, panDb.length);
      gl.uniform1f(uTraceDbMin, dbMin);
      gl.uniform1f(uTraceDbMax, dbMax);
      gl.uniform1f(uTraceOffsetPx, offsetPx);
      gl.uniform3f(uTraceColor, traceR, traceG, traceB);
      gl.drawArrays(gl.LINE_STRIP, 0, panDb.length);

      gl.useProgram(cursorProg);
      gl.bindVertexArray(cursorVao);
      gl.uniform3f(uCursorColor, 0.96, 0.74, 0.18);
      gl.drawArrays(gl.LINES, 0, 2);

      gl.bindVertexArray(null);
    },
    setTraceColor(r, g, b) {
      traceR = r;
      traceG = g;
      traceB = b;
    },
    dispose() {
      gl.deleteBuffer(traceVbo);
      gl.deleteBuffer(cursorVbo);
      gl.deleteTexture(panTex);
      gl.deleteVertexArray(traceVao);
      gl.deleteVertexArray(fillVao);
      gl.deleteVertexArray(cursorVao);
      gl.deleteProgram(traceProg);
      gl.deleteProgram(fillProg);
      gl.deleteProgram(cursorProg);
    },
  };
}

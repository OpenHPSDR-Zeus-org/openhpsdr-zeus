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

export const PAN_VS = /* glsl */ `#version 300 es
layout(location = 0) in float aDb;
uniform float uWidth;
uniform float uDbMin;
uniform float uDbMax;
uniform float uOffsetPx;
out float vLevel;
void main() {
  float x = (float(gl_VertexID) + 0.5 + uOffsetPx) / uWidth;
  float n = clamp((aDb - uDbMin) / (uDbMax - uDbMin), 0.0, 1.0);
  vLevel = n;
  gl_Position = vec4(x * 2.0 - 1.0, n * 2.0 - 1.0, 0.0, 1.0);
}`;

export const PAN_FS = /* glsl */ `#version 300 es
precision highp float;
in float vLevel;
uniform vec3 uColor;
uniform float uPopIntensity;
out vec4 fragColor;
vec3 popRamp(float n) {
  vec3 low = vec3(0.02, 0.20, 0.34);
  vec3 mid = vec3(0.00, 0.86, 1.00);
  vec3 hot = vec3(1.00, 0.72, 0.26);
  vec3 peak = vec3(1.00, 0.97, 0.82);
  vec3 c = mix(low, mid, smoothstep(0.04, 0.48, n));
  c = mix(c, hot, smoothstep(0.46, 0.82, n));
  return mix(c, peak, smoothstep(0.82, 1.0, n));
}
void main() {
  float popI = clamp(uPopIntensity, 0.0, 1.0);
  vec3 c = mix(uColor, popRamp(vLevel), popI);
  c += vec3(0.08, 0.12, 0.06) * smoothstep(0.58, 1.0, vLevel) * popI;
  c = min(c, vec3(1.0));
  fragColor = vec4(c, 1.0);
}`;

// Fill under the trace. Pan dB values live in a 1-row R32F texture; vertex
// IDs map 2i → bottom vertex for bin i, 2i+1 → top, so texelFetch at
// `gl_VertexID >> 1` yields the same dB for both verts of a bin. Rendered as
// a TRIANGLE_STRIP this produces one thin quad per bin, alpha-faded from 0
// at the floor to `uFillAlphaTop` at the trace for the warm-glow look.
export const PAN_FILL_VS = /* glsl */ `#version 300 es
uniform sampler2D uPan;
uniform float uWidth;
uniform float uDbMin;
uniform float uDbMax;
uniform float uOffsetPx;
uniform float uFillAlphaTop;
out float v_alpha;
out float v_level;
void main() {
  int binIdx = gl_VertexID >> 1;
  bool isTop = (gl_VertexID & 1) == 1;
  float aDb = texelFetch(uPan, ivec2(binIdx, 0), 0).r;
  float x = (float(binIdx) + 0.5 + uOffsetPx) / uWidth;
  float n = clamp((aDb - uDbMin) / (uDbMax - uDbMin), 0.0, 1.0);
  float y = isTop ? (n * 2.0 - 1.0) : -1.0;
  v_alpha = isTop ? uFillAlphaTop : 0.0;
  v_level = n;
  gl_Position = vec4(x * 2.0 - 1.0, y, 0.0, 1.0);
}`;

export const PAN_FILL_FS = /* glsl */ `#version 300 es
precision highp float;
in float v_alpha;
in float v_level;
uniform vec3 uColor;
uniform float uPopIntensity;
out vec4 fragColor;
vec3 popRamp(float n) {
  vec3 floorGlow = vec3(0.00, 0.10, 0.18);
  vec3 weak = vec3(0.00, 0.52, 0.78);
  vec3 strong = vec3(0.14, 0.96, 0.92);
  vec3 peak = vec3(1.00, 0.72, 0.30);
  vec3 c = mix(floorGlow, weak, smoothstep(0.02, 0.34, n));
  c = mix(c, strong, smoothstep(0.30, 0.70, n));
  return mix(c, peak, smoothstep(0.72, 1.0, n));
}
void main() {
  vec4 base = vec4(uColor * v_alpha, v_alpha);
  float popI = clamp(uPopIntensity, 0.0, 1.0);
  float lift = smoothstep(0.04, 0.92, v_level);
  float alpha = v_alpha * mix(0.42, 0.86, lift);
  vec3 c = popRamp(v_level) * alpha;
  fragColor = mix(base, vec4(c, alpha), popI);
}`;

export const CURSOR_VS = /* glsl */ `#version 300 es
layout(location = 0) in vec2 aPos;
void main() { gl_Position = vec4(aPos, 0.0, 1.0); }`;

export const CURSOR_FS = /* glsl */ `#version 300 es
precision highp float;
uniform vec3 uColor;
out vec4 fragColor;
void main() { fragColor = vec4(uColor, 0.6); }`;

// Waterfall quad: fullscreen triangle-pair, samples the history texture
// with a vertical rolling offset so the newest row is at the top.
export const WF_VS = /* glsl */ `#version 300 es
layout(location = 0) in vec2 aPos;
out vec2 vUv;
void main() {
  vUv = aPos * 0.5 + 0.5;
  gl_Position = vec4(aPos, 0.0, 1.0);
}`;

export const WF_FS = /* glsl */ `#version 300 es
precision highp float;
in vec2 vUv;
uniform sampler2D uHistory;
uniform sampler2D uLut;
uniform float uDbMin;
uniform float uDbMax;
uniform float uWriteRow;
uniform float uH;
uniform float uVisibleRows;
uniform float uScrollSpeed;
uniform float uBgAlpha;
uniform float uViewOffsetUv;
uniform float uSeedDb;
uniform float uPopActive;
uniform float uPopIntensity;
uniform float uReliefDepth;
uniform float uSmoothness;
uniform float uTexW;
out vec4 fragColor;

float sampleDb(vec2 uv) {
  vec2 clampedUv = vec2(uv.x, clamp(uv.y, 0.0, 1.0));
  float ageRows = (1.0 - clampedUv.y) * max(1.0, uVisibleRows) / max(0.05, uScrollSpeed);
  if (ageRows > uH - 2.0) return uSeedDb;
  float row = mod(uWriteRow - ageRows + uH, uH);
  float srcX = clampedUv.x - uViewOffsetUv;
  return (srcX < 0.0 || srcX > 1.0)
    ? uSeedDb
    : texture(uHistory, vec2(srcX, (row + 0.5) / uH)).r;
}

float toLevel(float db) {
  return clamp((db - uDbMin) / max(0.0001, uDbMax - uDbMin), 0.0, 1.0);
}

float sampleLevel(vec2 uv) {
  return toLevel(sampleDb(uv));
}

float closeWeight(float center, float value, float width) {
  return 1.0 - smoothstep(width * 0.34, width, abs(center - value));
}

float popLift(float n, float popI) {
  float lifted = pow(n, mix(1.0, 0.62, popI));
  float micro = smoothstep(0.015, 0.20, n) * 0.10 * popI;
  return clamp(max(lifted, n + micro), 0.0, 1.0);
}

void main() {
  float n = sampleLevel(vUv);
  float popI = clamp(uPopIntensity, 0.0, 1.0) * step(0.5, uPopActive);
  float normalI = 1.0 - step(0.5, uPopActive);
  float normalHdrI = normalI * 0.54;
  float normalReliefI = normalI * 0.46;
  float reliefI = clamp(uReliefDepth, 0.0, 1.0) * popI;
  float smoothI = clamp(uSmoothness, 0.0, 1.0) * popI;
  float shade = 1.0;
  float ridge = 0.0;
  float rim = 0.0;
  float shadow = 0.0;
  float specular = 0.0;
  float warmHalo = 0.0;
  float coolHalo = 0.0;
  float offsetGlow = 0.0;
  float castGlow = 0.0;
  float contour = 0.0;
  float heightContour = 0.0;
  float crest = 0.0;
  if (popI > 0.001 || normalHdrI > 0.001) {
    vec2 texel = vec2(1.0 / max(1.0, uTexW), 1.0 / max(1.0, uH));
    float left = sampleLevel(vUv - vec2(texel.x, 0.0));
    float right = sampleLevel(vUv + vec2(texel.x, 0.0));
    float older = sampleLevel(vUv - vec2(0.0, texel.y));
    float newer = sampleLevel(vUv + vec2(0.0, texel.y));
    float left2 = sampleLevel(vUv - vec2(texel.x * 2.0, 0.0));
    float right2 = sampleLevel(vUv + vec2(texel.x * 2.0, 0.0));
    float older2 = sampleLevel(vUv - vec2(0.0, texel.y * 2.0));
    float newer2 = sampleLevel(vUv + vec2(0.0, texel.y * 2.0));
    float diagA = sampleLevel(vUv + vec2(-texel.x, texel.y));
    float diagB = sampleLevel(vUv + vec2(texel.x, texel.y));
    float diagC = sampleLevel(vUv + vec2(-texel.x, -texel.y));
    float diagD = sampleLevel(vUv + vec2(texel.x, -texel.y));
    float cross = (left + right + older + newer) * 0.25;
    float popReliefCurve = smoothstep(0.01, 0.52, clamp(uReliefDepth, 0.0, 1.0)) * popI;
    float edgeEnergy = abs(right - left) + abs(newer - older);
    float reliefSignal = max(0.0, n - cross) + edgeEnergy * 0.55;
    float reliefCurve = max(popReliefCurve, normalReliefI * smoothstep(0.010, 0.25, reliefSignal));
    float closeWidth = mix(0.16, 0.28, smoothI);
    float wL = closeWeight(n, left, closeWidth);
    float wR = closeWeight(n, right, closeWidth);
    float wO = closeWeight(n, older, closeWidth);
    float wN = closeWeight(n, newer, closeWidth);
    float wL2 = closeWeight(n, left2, closeWidth) * 0.45;
    float wR2 = closeWeight(n, right2, closeWidth) * 0.45;
    float wO2 = closeWeight(n, older2, closeWidth) * 0.35;
    float wN2 = closeWeight(n, newer2, closeWidth) * 0.35;
    float wD = 0.18;
    float weighted =
      n * 1.85 + left * wL + right * wR + older * wO + newer * wN +
      left2 * wL2 + right2 * wR2 + older2 * wO2 + newer2 * wN2 +
      (diagA + diagB + diagC + diagD) * wD;
    float weights = 1.85 + wL + wR + wO + wN + wL2 + wR2 + wO2 + wN2 + 4.0 * wD;
    float smoothLevel = weighted / max(0.0001, weights);
    float edgeGuard = smoothstep(0.030, 0.24, edgeEnergy);
    float lowMid = smoothstep(0.015, 0.58, n) * (1.0 - smoothstep(0.72, 1.0, n));
    n = clamp(mix(n, pow(n, 0.78), normalHdrI) + lowMid * 0.10 * normalHdrI, 0.0, 1.0);
    float smoothAmount = max(
      normalI * 0.24,
      smoothI * mix(0.84, 0.30, edgeGuard) * (1.0 - smoothstep(0.86, 1.0, n) * 0.45));
    n = mix(n, smoothLevel, clamp(smoothAmount, 0.0, 0.92));
    crest = max(0.0, n - smoothLevel);

    if (reliefCurve > 0.001) {
      float upLeft = sampleLevel(vUv + vec2(-texel.x * 1.35, texel.y * 1.35));
      float downRight = sampleLevel(vUv + vec2(texel.x * 1.65, -texel.y * 1.65));
      float reliefPx = mix(1.2, 6.2, reliefCurve);
      float raisedA = max(
        sampleLevel(vUv + vec2(-texel.x * reliefPx, texel.y * reliefPx)),
        sampleLevel(vUv + vec2(-texel.x * reliefPx * 0.55, texel.y * reliefPx * 0.55)));
      float raisedB = max(
        sampleLevel(vUv + vec2(texel.x * reliefPx, -texel.y * reliefPx)),
        sampleLevel(vUv + vec2(texel.x * reliefPx * 0.55, -texel.y * reliefPx * 0.55)));
      float nearMax = max(max(left, right), max(older, newer));
      float bevel = n - cross;
      n = clamp(
        n + bevel * mix(0.50, 1.58, reliefCurve) + max(bevel, 0.0) * 0.16 * reliefCurve,
        0.0,
        1.0);
      float slope = mix(24.0, 78.0, reliefCurve);
      vec3 normal = normalize(vec3((left - right) * slope, (older - newer) * slope, 1.0));
      vec3 lightDir = normalize(vec3(-0.62, 0.76, 0.78));
      float lambert = clamp(dot(normal, lightDir) * 0.5 + 0.5, 0.0, 1.0);
      shade = 0.22 + 1.62 * pow(lambert, 1.55);
      ridge = smoothstep(0.006, 0.082, edgeEnergy) *
        smoothstep(0.014, 0.24, n) * reliefCurve;
      rim = smoothstep(0.020, 0.42, n) * smoothstep(0.004, 0.16, max(0.0, n - downRight)) *
        reliefCurve;
      shadow = smoothstep(0.035, 0.48, upLeft) * (1.0 - smoothstep(0.014, 0.24, n)) *
        reliefCurve;
      warmHalo = smoothstep(0.055, 0.45, downRight) * (1.0 - smoothstep(0.018, 0.26, n)) *
        reliefCurve;
      coolHalo = smoothstep(0.045, 0.40, nearMax) * (1.0 - smoothstep(0.020, 0.30, n)) *
        reliefCurve;
      castGlow = smoothstep(0.028, 0.40, raisedA) * (1.0 - smoothstep(0.020, 0.30, n)) *
        reliefCurve;
      offsetGlow = smoothstep(0.028, 0.38, raisedB) * (1.0 - smoothstep(0.018, 0.28, n)) *
        reliefCurve;
      contour = smoothstep(0.035, 0.42, n) * smoothstep(0.004, 0.10, edgeEnergy) *
        reliefCurve;
      float contourPhase = abs(fract(n * 9.5) - 0.5);
      heightContour = (1.0 - smoothstep(0.030, 0.095, contourPhase)) *
        smoothstep(0.10, 0.96, n) * reliefCurve;
      vec3 viewDir = vec3(0.0, 0.0, 1.0);
      specular = pow(max(dot(reflect(-lightDir, normal), viewDir), 0.0), 13.0) *
        reliefCurve * smoothstep(0.10, 0.82, n);
      specular += crest * reliefCurve * 0.86;
      reliefI = reliefCurve;
    }
  }
  n = popLift(n, popI);
  vec4 c = texture(uLut, vec2(n, 0.5));
  c.rgb *= mix(1.0, shade, reliefI);
  c.rgb = mix(c.rgb, vec3(0.00, 0.030, 0.055) + c.rgb * 0.24, min(0.84, 0.82 * shadow));
  c.rgb = mix(c.rgb, vec3(0.00, 0.070, 0.16), min(0.78, castGlow * 0.82));
  c.rgb += vec3(0.00, 0.78, 1.00) * offsetGlow * 0.58;
  c.rgb += vec3(0.70, 0.42, 0.10) * warmHalo * 0.86;
  c.rgb += vec3(0.00, 0.18, 0.32) * coolHalo * 0.72;
  c.rgb += vec3(0.04, 0.54, 0.60) * ridge * 1.18;
  c.rgb += vec3(0.36, 0.88, 0.90) * rim * 1.25;
  c.rgb += vec3(0.98, 1.00, 0.68) * contour * 0.66;
  c.rgb += vec3(1.00, 0.82, 0.34) * heightContour * 0.34;
  c.rgb += vec3(0.28, 0.96, 1.00) * crest * reliefI * 0.62;
  c.rgb += vec3(1.00, 0.92, 0.48) * specular * 1.18;
  c.rgb = min(c.rgb, vec3(1.0));
  float reliefMask = max(
    max(castGlow, offsetGlow),
    max(max(warmHalo, coolHalo), max(max(ridge, rim), heightContour)));
  float a = mix(smoothstep(0.05, 0.9, n), 1.0, uBgAlpha);
  a = max(a, reliefMask * mix(0.45, 0.98, reliefI));
  fragColor = vec4(c.rgb * a, a);
}`;

// Horizontal remap pass for doc 08 §5 ping-pong: sample the previous history
// at the absolute-frequency equivalent of the destination x. Plain retunes
// use scale=1 and an offset; zoom changes use scale=nextHzPerPixel/oldHzPerPixel
// so the old waterfall history narrows/widens around the new center instead
// of being wiped. Rendered into the inactive R32F texture; the main WF_FS then
// reads from the now-active texture next draw.
export const WF_REMAP_FS = /* glsl */ `#version 300 es
precision highp float;
in vec2 vUv;
uniform sampler2D uSrc;
uniform float uSrcXScale;
uniform float uSrcCenterOffsetUv;
uniform float uSeedDb;
layout(location = 0) out vec4 fragColor;
void main() {
  float srcX = 0.5 + uSrcCenterOffsetUv + (vUv.x - 0.5) * uSrcXScale;
  float v = (srcX < 0.0 || srcX > 1.0)
    ? uSeedDb
    : texture(uSrc, vec2(srcX, vUv.y)).r;
  fragColor = vec4(v, 0.0, 0.0, 1.0);
}`;

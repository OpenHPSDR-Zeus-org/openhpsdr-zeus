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

export const PAN_VS = /* glsl */ `#version 300 es
layout(location = 0) in float aDb;
uniform float uWidth;
uniform float uDbMin;
uniform float uDbMax;
uniform float uOffsetPx;
// Draw-time zoom: scale the bin position about the view centre (0.5). 1.0 is
// no zoom; <1 packs bins toward centre (mid-glide of a zoom-in), easing to 1
// as the displayed span catches up to the server span. See view-zoom.ts.
uniform float uScaleX;
out float vLevel;
void main() {
  float x = (float(gl_VertexID) + 0.5 + uOffsetPx) / uWidth;
  x = 0.5 + (x - 0.5) * uScaleX;
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
// Draw-time zoom about the view centre — must match PAN_VS so the fill stays
// under the trace mid-glide. See view-zoom.ts.
uniform float uScaleX;
uniform float uFillAlphaTop;
out float v_alpha;
out float v_level;
void main() {
  int binIdx = gl_VertexID >> 1;
  bool isTop = (gl_VertexID & 1) == 1;
  float aDb = texelFetch(uPan, ivec2(binIdx, 0), 0).r;
  float x = (float(binIdx) + 0.5 + uOffsetPx) / uWidth;
  x = 0.5 + (x - 0.5) * uScaleX;
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
uniform sampler2D uTerrainHistory;
uniform sampler2D uLut;
uniform float uDbMin;
uniform float uDbMax;
uniform float uWriteRow;
uniform float uH;
uniform float uVisibleRows;
uniform float uScrollSpeed;
uniform float uValidRows;
uniform float uBgAlpha;
uniform float uViewOffsetUv;
uniform float uViewScale;
uniform float uSeedDb;
uniform float uPopActive;
uniform float uPopIntensity;
uniform float uReliefDepth;
uniform float uSmoothness;
uniform float uTexW;
uniform float uCanvasW;
out vec4 fragColor;

float sampleDb(vec2 uv) {
  vec2 clampedUv = vec2(uv.x, clamp(uv.y, 0.0, 1.0));
  if (uValidRows < 0.5) return uSeedDb;
  float ageRows = (1.0 - clampedUv.y) * max(1.0, uVisibleRows) / max(0.05, uScrollSpeed);
  float maxAgeRows = max(0.0, min(uH - 2.0, uValidRows - 1.0));
  if (ageRows > maxAgeRows + 0.001) return uSeedDb;
  float row = mod(uWriteRow - ageRows + uH, uH);
  // Draw-time zoom: scale the sampled column about the view centre (0.5), then
  // apply the sub-pixel pan offset. uViewScale = displayedHzPerPixel /
  // historyHzPerPixel — 1.0 is no zoom; >1 mid-glide of a zoom-in samples a
  // narrower centre slice (carriers spread apart as it eases to 1). The
  // history texture is never re-resampled for this; it's a pure sampling
  // transform. See view-zoom.ts.
  float srcX = 0.5 + (clampedUv.x - 0.5) * uViewScale - uViewOffsetUv;
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

float sampleTerrain(vec2 uv) {
  vec2 clampedUv = vec2(uv.x, clamp(uv.y, 0.0, 1.0));
  if (uValidRows < 0.5) return 0.0;
  float ageRows = (1.0 - clampedUv.y) * max(1.0, uVisibleRows) / max(0.05, uScrollSpeed);
  float maxAgeRows = max(0.0, min(uH - 2.0, uValidRows - 1.0));
  if (ageRows > maxAgeRows + 0.001) return 0.0;
  float row = mod(uWriteRow - ageRows + uH, uH);
  float srcX = 0.5 + (clampedUv.x - 0.5) * uViewScale - uViewOffsetUv;
  return (srcX < 0.0 || srcX > 1.0)
    ? 0.0
    : clamp(texture(uTerrainHistory, vec2(srcX, (row + 0.5) / uH)).r, 0.0, 1.0);
}

float closeWeight(float center, float value, float width) {
  return 1.0 - smoothstep(width * 0.34, width, abs(center - value));
}

float popLift(float n, float popI) {
  float lifted = pow(n, mix(1.0, 1.28, popI));
  float micro = smoothstep(0.050, 0.24, n) * 0.010 * popI;
  return clamp(max(lifted, n + micro), 0.0, 1.0);
}

vec3 terrainRamp(float h) {
  vec3 bathy = vec3(0.00, 0.08, 0.20);
  vec3 shelf = vec3(0.00, 0.42, 0.58);
  vec3 slope = vec3(0.12, 0.70, 0.54);
  vec3 ridge = vec3(0.72, 0.54, 0.28);
  vec3 summit = vec3(0.96, 0.84, 0.54);
  vec3 c = mix(bathy, shelf, smoothstep(0.02, 0.26, h));
  c = mix(c, slope, smoothstep(0.18, 0.52, h));
  c = mix(c, ridge, smoothstep(0.46, 0.78, h));
  c = mix(c, summit, smoothstep(0.76, 0.98, h));
  return c;
}

void main() {
  float n = sampleLevel(vUv);
  float popI = clamp(uPopIntensity, 0.0, 1.0) * step(0.5, uPopActive);
  float normalI = 1.0 - step(0.5, uPopActive);
  // Binary Signal-Pop mode gate (1 in Pop, 0 in normal RX). The hillshade relief
  // (multiplicative shade) is kept in BOTH modes — that is the topographic look
  // the WebGPU heightfield also has and it does not over-expose on its own. But
  // the additive glow/halo/terrain decoration and the carrier level-lifts below
  // bloomed the noise field and washed the colours out in normal RX. Gating them
  // to Pop makes normal RX render as plain colormap × hillshade, matching the
  // WebGPU heightfield tone, while leaving the Pop look byte-for-byte unchanged.
  float popModeI = step(0.5, uPopActive);
  float reliefDepthI = clamp(uReliefDepth, 0.0, 1.0);
  float normalHdrI = normalI * mix(0.14, 0.24, reliefDepthI);
  // Toned down from 0.72..1.30: the legacy normal-mode relief stacked many
  // additive glow/contour terms that brightened the noise field and produced
  // horizontal banding the WebGPU heightfield doesn't have. Lower intensity keeps
  // a hint of relief while matching the cleaner heightfield look.
  float normalReliefI = normalI * mix(0.34, 0.62, reliefDepthI);
  float reliefI = clamp(uReliefDepth, 0.0, 1.0) * popI;
  float smoothI = clamp(uSmoothness, 0.0, 1.0) * max(popI, normalI);
  float terrainModeI = max(popI, normalI * smoothstep(0.10, 0.82, reliefDepthI));
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
  float dropShadow = 0.0;
  float goldRim = 0.0;
  float terrainShadow = 0.0;
  float terrainRim = 0.0;
  float terrainContourDark = 0.0;
  float terrainTint = 0.0;
  vec3 terrainColor = vec3(0.0);
  float terrainDisplayHeight = 0.0;
  float crest = 0.0;
  if (popI > 0.001 || normalHdrI > 0.001) {
    vec2 px = vec2(1.0 / max(1.0, uCanvasW), 1.0 / max(1.0, uVisibleRows));
    float h = sampleTerrain(vUv);
    float left = sampleLevel(vUv - vec2(px.x, 0.0));
    float right = sampleLevel(vUv + vec2(px.x, 0.0));
    float older = sampleLevel(vUv - vec2(0.0, px.y));
    float newer = sampleLevel(vUv + vec2(0.0, px.y));
    float left2 = sampleLevel(vUv - vec2(px.x * 2.0, 0.0));
    float right2 = sampleLevel(vUv + vec2(px.x * 2.0, 0.0));
    float older2 = sampleLevel(vUv - vec2(0.0, px.y * 2.0));
    float newer2 = sampleLevel(vUv + vec2(0.0, px.y * 2.0));
    float diagA = sampleLevel(vUv + vec2(-px.x, px.y));
    float diagB = sampleLevel(vUv + vec2(px.x, px.y));
    float diagC = sampleLevel(vUv + vec2(-px.x, -px.y));
    float diagD = sampleLevel(vUv + vec2(px.x, -px.y));
    float x3L = sampleLevel(vUv - vec2(px.x * 3.0, 0.0));
    float x3R = sampleLevel(vUv + vec2(px.x * 3.0, 0.0));
    float x6L = sampleLevel(vUv - vec2(px.x * 6.0, 0.0));
    float x6R = sampleLevel(vUv + vec2(px.x * 6.0, 0.0));
    float y3O = sampleLevel(vUv - vec2(0.0, px.y * 3.0));
    float y3N = sampleLevel(vUv + vec2(0.0, px.y * 3.0));
    float hLeft = sampleTerrain(vUv - vec2(px.x, 0.0));
    float hRight = sampleTerrain(vUv + vec2(px.x, 0.0));
    float hOlder = sampleTerrain(vUv - vec2(0.0, px.y));
    float hNewer = sampleTerrain(vUv + vec2(0.0, px.y));
    float hLeft2 = sampleTerrain(vUv - vec2(px.x * 2.0, 0.0));
    float hRight2 = sampleTerrain(vUv + vec2(px.x * 2.0, 0.0));
    float hX3L = sampleTerrain(vUv - vec2(px.x * 3.0, 0.0));
    float hX3R = sampleTerrain(vUv + vec2(px.x * 3.0, 0.0));
    float hX6L = sampleTerrain(vUv - vec2(px.x * 6.0, 0.0));
    float hX6R = sampleTerrain(vUv + vec2(px.x * 6.0, 0.0));
    float hY3O = sampleTerrain(vUv - vec2(0.0, px.y * 3.0));
    float hY3N = sampleTerrain(vUv + vec2(0.0, px.y * 3.0));
    float levelNearShoulder = max(max(left, right) * 0.80, max(left2, right2) * 0.62);
    float levelWideShoulder = max(max(x3L, x3R) * 0.42, max(x6L, x6R) * 0.26);
    float nearShoulder = max(max(hLeft, hRight) * 0.98, max(hLeft2, hRight2) * 0.88);
    float wideShoulder = max(max(hX3L, hX3R) * 0.76, max(hX6L, hX6R) * 0.48);
    float timeShoulder = max(max(hOlder, hNewer) * 0.40, max(hY3O, hY3N) * 0.34);
    float shoulderSeed = max(max(nearShoulder, wideShoulder), timeShoulder);
    shoulderSeed = max(shoulderSeed, max(levelNearShoulder, levelWideShoulder) * terrainModeI * 0.34);
    float ridgeSource = max(max(h, n * 0.18 * terrainModeI), shoulderSeed);
    float cross = (left + right + older + newer) * 0.25;
    float hCross = (hLeft + hRight + hOlder + hNewer) * 0.25;
    float popReliefCurve = mix(0.76, 1.0, smoothstep(0.01, 0.52, clamp(uReliefDepth, 0.0, 1.0))) * popI;
    float edgeEnergy = abs(right - left) + abs(newer - older);
    float heightEdgeEnergy = abs(hRight - hLeft) + abs(hNewer - hOlder);
    float reliefSignal = max(0.0, ridgeSource - hCross) + max(edgeEnergy * 0.22, heightEdgeEnergy * 0.92);
    float normalReliefCurve = normalReliefI *
      smoothstep(0.025, 0.26, reliefSignal) *
      smoothstep(0.024, 0.28, ridgeSource);
    float reliefCurve = max(popReliefCurve, normalReliefCurve);
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
    n = clamp(mix(n, pow(n, 0.82), normalHdrI) + lowMid * 0.025 * normalHdrI, 0.0, 1.0);
    float smoothAmount = max(
      normalI * 0.14,
      smoothI * mix(0.84, 0.30, edgeGuard) * (1.0 - smoothstep(0.86, 1.0, n) * 0.45));
    n = mix(n, smoothLevel, clamp(smoothAmount, 0.0, 0.92));
    float shoulderLift = shoulderSeed * reliefCurve * smoothstep(0.035, 0.50, ridgeSource);
    n = max(n, shoulderLift * mix(0.52, 0.82, reliefCurve) * popModeI);
    crest = max(0.0, n - smoothLevel);

    if (reliefCurve > 0.001) {
      float upLeft = sampleTerrain(vUv + vec2(-px.x * 1.35, px.y * 1.35));
      float downRight = sampleTerrain(vUv + vec2(px.x * 1.65, -px.y * 1.65));
      float terrainHeight = max(max(h, shoulderSeed * mix(0.82, 1.14, reliefCurve)), n * 0.16 * terrainModeI);
      float height = smoothstep(0.012, 0.76, terrainHeight);
      terrainDisplayHeight = height;
      float reliefPx = mix(6.0, 30.0, reliefCurve) * (0.46 + height * 0.62);
      float raisedA = max(
        sampleTerrain(vUv + vec2(-px.x * reliefPx, px.y * reliefPx)),
        sampleTerrain(vUv + vec2(-px.x * reliefPx * 0.55, px.y * reliefPx * 0.55)));
      float raisedB = max(
        sampleTerrain(vUv + vec2(px.x * reliefPx, -px.y * reliefPx)),
        sampleTerrain(vUv + vec2(px.x * reliefPx * 0.55, -px.y * reliefPx * 0.55)));
      float castA = max(
        sampleTerrain(vUv + vec2(-px.x * reliefPx * 1.45, px.y * reliefPx * 1.45)),
        sampleTerrain(vUv + vec2(-px.x * reliefPx * 2.25, px.y * reliefPx * 2.00)));
      float rimSource = max(
        sampleTerrain(vUv + vec2(px.x * reliefPx * 0.75, -px.y * reliefPx * 0.75)),
        sampleTerrain(vUv + vec2(px.x * reliefPx * 1.35, -px.y * reliefPx * 1.15)));
      float nearMax = max(max(hLeft, hRight), max(hOlder, hNewer));
      float bevel = terrainHeight - hCross;
      float valueBevelGain = mix(0.82, 2.70, reliefCurve) * mix(1.0, 0.34, popI);
      n = clamp(
        n + (bevel * valueBevelGain + max(bevel, 0.0) * (0.22 + height * 0.30) * reliefCurve * mix(1.0, 0.42, popI)) * popModeI,
        0.0,
        1.0);
      float slope = mix(34.0, 116.0, reliefCurve);
      vec3 normal = normalize(vec3((hLeft - hRight) * slope, (hOlder - hNewer) * slope, 1.0));
      vec3 lightDir = normalize(vec3(-0.70, 0.78, 0.66));
      float lambert = clamp(dot(normal, lightDir) * 0.5 + 0.5, 0.0, 1.0);
      shade = 0.14 + 1.94 * pow(lambert, 1.34);
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
      dropShadow = smoothstep(0.035, 0.55, raisedA) * (1.0 - smoothstep(0.030, 0.34, n)) *
        reliefCurve * (0.75 + height * 0.55);
      goldRim = smoothstep(0.045, 0.74, n) * smoothstep(0.006, 0.18, max(0.0, n - upLeft)) *
        reliefCurve * (0.70 + height * 0.65);
      terrainShadow = smoothstep(0.040, 0.60, castA) * (1.0 - smoothstep(0.035, 0.35, n)) *
        reliefCurve * (0.85 + height * 0.65);
      terrainRim = smoothstep(0.035, 0.84, n) * smoothstep(0.006, 0.22, max(0.0, n - rimSource)) *
        reliefCurve * (0.75 + height * 0.75);
      contour = smoothstep(0.035, 0.42, n) * smoothstep(0.004, 0.10, edgeEnergy) *
        reliefCurve;
      float contourPhase = abs(fract(n * 9.5) - 0.5);
      heightContour = (1.0 - smoothstep(0.030, 0.095, contourPhase)) *
        smoothstep(0.10, 0.96, n) * reliefCurve;
      float terrainContourPhase = abs(fract(height * 14.0 + reliefCurve * 0.12) - 0.5);
      terrainContourDark = (1.0 - smoothstep(0.010, 0.040, terrainContourPhase)) *
        smoothstep(0.16, 0.98, height) * reliefCurve;
      terrainTint = smoothstep(0.014, 0.64, terrainHeight) * reliefCurve;
      terrainColor = terrainRamp(height);
      vec3 viewDir = vec3(0.0, 0.0, 1.0);
      specular = pow(max(dot(reflect(-lightDir, normal), viewDir), 0.0), 13.0) *
        reliefCurve * smoothstep(0.10, 0.82, n) * (0.70 + height * 0.75);
      specular += crest * reliefCurve * 0.86;
      specular *= mix(1.0, 0.30, popI);
      reliefI = reliefCurve;
    }
  }
  n = popLift(n, popI);
  // Pop signal ceiling. The cap reserves the bright top of the ramp for the
  // hillshade terrain relief, but at 0.68 even strong carriers topped out around
  // light-blue and read as thin streaks instead of popping. Lifted so real
  // signals reach the brighter end of the Pop palette (closer to how the normal
  // textured waterfall lets them pop) while terrain still has headroom above.
  float colorLevel = mix(n, min(n, 0.82 + terrainDisplayHeight * 0.12), popI);
  // Match the WebGPU heightfield tone: in normal RX mode pull the noise floor
  // down with a gamma so it reads dark and signals separate, instead of the
  // legacy lifted/harsh curve. Gated to normal mode (Pop/TX keep their mapping).
  colorLevel = mix(colorLevel, pow(colorLevel, 1.5), normalI);
  vec4 c = texture(uLut, vec2(colorLevel, 0.5));
  // Hillshade relief multiply — applied in BOTH modes (this is the topographic
  // look the WebGPU heightfield also uses; it does not over-expose on its own).
  c.rgb *= mix(1.0, shade, reliefI);
  // Everything below is Signal-Pop decoration (terrain tint, cast shadows, edge
  // glows/halos, rim light, contour lines, crest/specular sheen). popModeI is 0
  // in normal RX — so there the surface is plain colormap × hillshade and matches
  // the WebGPU heightfield instead of blooming the noise field (over-exposure
  // fix) — and 1 in Pop, so the Pop look is unchanged.
  vec3 shadedTerrain = terrainColor * mix(vec3(0.60), vec3(1.18), clamp(shade * 0.58, 0.0, 1.0));
  c.rgb = mix(c.rgb, shadedTerrain, min(0.99, terrainTint * mix(0.82, 1.0, terrainModeI)) * popModeI);
  c.rgb = mix(c.rgb, vec3(0.0, 0.004, 0.010), min(0.95, max(dropShadow * 1.08, terrainShadow * 1.22)) * popModeI);
  c.rgb = mix(c.rgb, vec3(0.00, 0.030, 0.055) + c.rgb * 0.24, min(0.84, 0.82 * shadow) * popModeI);
  c.rgb = mix(c.rgb, vec3(0.00, 0.055, 0.13), min(0.84, castGlow * 1.05) * popModeI);
  c.rgb += vec3(0.00, 0.78, 1.00) * offsetGlow * 0.72 * popModeI;
  c.rgb += vec3(0.70, 0.42, 0.10) * warmHalo * 1.02 * popModeI;
  c.rgb += vec3(0.00, 0.18, 0.32) * coolHalo * 0.72 * popModeI;
  c.rgb += vec3(0.04, 0.54, 0.60) * ridge * 1.18 * popModeI;
  c.rgb += vec3(0.36, 0.88, 0.90) * rim * 1.52 * popModeI;
  c.rgb += vec3(0.98, 1.00, 0.68) * contour * 0.82 * popModeI;
  c.rgb += vec3(1.00, 0.82, 0.34) * heightContour * 0.54 * popModeI;
  c.rgb = mix(c.rgb, c.rgb * vec3(0.48, 0.38, 0.26), min(0.84, terrainContourDark * 0.92) * popModeI);
  c.rgb += vec3(1.00, 0.66, 0.16) * goldRim * mix(1.18, 0.44, popI) * popModeI;
  c.rgb += vec3(1.00, 0.92, 0.60) * terrainRim * mix(1.34, 0.50, popI) * popModeI;
  c.rgb += vec3(0.28, 0.96, 1.00) * crest * reliefI * mix(0.78, 0.34, popI) * popModeI;
  c.rgb += vec3(1.00, 0.92, 0.48) * specular * mix(1.48, 0.38, popI) * popModeI;
  c.rgb = min(c.rgb, mix(vec3(1.0), vec3(0.92, 0.86, 0.68), popI * terrainTint * 0.82));
  c.rgb = min(c.rgb, vec3(1.0));
  float reliefMask = max(
    max(castGlow, offsetGlow),
    max(
      max(warmHalo, coolHalo),
      max(
        max(max(ridge, rim), max(heightContour, terrainContourDark)),
        max(max(dropShadow, terrainShadow), max(goldRim, terrainRim)))));
  float a = mix(smoothstep(0.05, 0.9, n), 1.0, uBgAlpha);
  // Decoration masks raise alpha only in Pop; in normal RX the floor keeps its
  // smoothstep transparency instead of being forced opaque by the relief terms.
  a = max(a, reliefMask * mix(0.45, 0.98, reliefI) * popModeI);
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

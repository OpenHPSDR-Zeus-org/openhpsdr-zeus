#!/usr/bin/env node
// SPDX-License-Identifier: GPL-2.0-or-later
//
// Generate the VST-engine download manifest (vst-engine/latest.json) consumed
// by Zeus's in-app "Download VST Engine" / auto-repair flow (VstEngineInstaller).
//
// Zeus stages the engine named here and verifies its SHA-256 before use, so the
// operator always gets a known-good, bridge-compatible binary — NOT a floating
// upstream "latest" (which has shipped a build that crash-loops the handshake).
//
// Usage:
//   node tools/vst-engine-manifest.mjs <VSTHostEngine.exe> <version> [out.json]
//
// Env:
//   VST_ENGINE_BASE_URL   default https://downloads.openhpsdrzeus.com/vst-engine
//   VST_ENGINE_PUBLISHED  ISO timestamp; default omitted
//
// The engine binary itself must be uploaded next to latest.json on the domain as
// the versioned filename printed below (e.g. VSTHostEngine-2026.06.14.exe), so a
// new manifest never invalidates a cached older binary.

import { createHash } from "node:crypto";
import { readFileSync, statSync, writeFileSync } from "node:fs";
import path from "node:path";

const [enginePath, version, outPath] = process.argv.slice(2);
if (!enginePath || !version) {
  console.error("Usage: node tools/vst-engine-manifest.mjs <VSTHostEngine.exe> <version> [out.json]");
  process.exit(2);
}

const baseUrl = (process.env.VST_ENGINE_BASE_URL || "https://downloads.openhpsdrzeus.com/vst-engine")
  .replace(/\/+$/, "");
const bytes = readFileSync(enginePath);
const sha256 = createHash("sha256").update(bytes).digest("hex");
const size = statSync(enginePath).size;

// Versioned, immutable download name so publishing a new manifest never breaks a
// cached older engine. Always staged as VSTHostEngine.exe on the client side.
const ext = path.extname(enginePath) || ".exe";
const downloadName = `VSTHostEngine-${version}${ext}`;

const manifest = {
  version,
  ...(process.env.VST_ENGINE_PUBLISHED ? { publishedAt: process.env.VST_ENGINE_PUBLISHED } : {}),
  assets: [
    {
      filename: downloadName,
      url: `${baseUrl}/${downloadName}`,
      size,
      sha256,
      platform: "windows",
      arch: "x64",
    },
  ],
};

const json = JSON.stringify(manifest, null, 2) + "\n";
if (outPath) {
  writeFileSync(outPath, json);
  console.error(`Wrote ${outPath}`);
} else {
  process.stdout.write(json);
}
console.error(`\nUpload the engine as: ${baseUrl}/${downloadName}`);
console.error(`Upload the manifest as: ${baseUrl}/latest.json`);
console.error(`sha256=${sha256} size=${size}`);

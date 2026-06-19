#!/usr/bin/env node
import { createHash } from "node:crypto";
import { existsSync, readdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import path from "node:path";

const [artifactRoot, existingPath, outputPath] = process.argv.slice(2);

if (!artifactRoot || !existingPath || !outputPath) {
  console.error("Usage: node tools/update-download-manifest.mjs <artifact-root> <existing-manifest.json> <output-manifest.json>");
  process.exit(2);
}

const baseUrl = process.env.DOWNLOAD_BASE_URL || "https://downloads.openhpsdrzeus.com";
const files = walk(artifactRoot).filter((file) => isDownload(file));
if (files.length === 0) {
  console.error(`No downloadable artifacts found under ${artifactRoot}`);
  process.exit(1);
}

const version = process.env.ZEUS_DOWNLOAD_VERSION || inferVersion(files);
if (!version) {
  console.error("Could not infer Zeus version from artifact filenames.");
  process.exit(1);
}

const manifest = loadManifest(existingPath);
const assets = files
  .map((file) => assetFor(file, version, baseUrl))
  .filter(Boolean)
  .sort(assetSort);

if (assets.length === 0) {
  console.error(`No recognized Zeus artifacts found for ${version}.`);
  process.exit(1);
}

const versionEntry = {
  version,
  channel: process.env.ZEUS_DOWNLOAD_CHANNEL || "main",
  publishedAt: new Date().toISOString(),
  source: {
    branch: process.env.ZEUS_DOWNLOAD_BRANCH || process.env.GITHUB_REF_NAME || "main",
    commit: process.env.GITHUB_SHA || null,
    runUrl: process.env.GITHUB_SERVER_URL && process.env.GITHUB_REPOSITORY && process.env.GITHUB_RUN_ID
      ? `${process.env.GITHUB_SERVER_URL}/${process.env.GITHUB_REPOSITORY}/actions/runs/${process.env.GITHUB_RUN_ID}`
      : null,
  },
  assets,
};

const versions = [
  versionEntry,
  ...manifest.versions.filter((entry) => entry.version !== version),
].sort((a, b) => String(b.publishedAt || "").localeCompare(String(a.publishedAt || "")));

const nextManifest = {
  schema: 1,
  updatedAt: versionEntry.publishedAt,
  latest: version,
  versions,
};

writeFileSync(outputPath, `${JSON.stringify(nextManifest, null, 2)}\n`, "utf8");
writeFileSync(path.join(path.dirname(outputPath), "latest.json"), `${JSON.stringify(versionEntry, null, 2)}\n`, "utf8");

function walk(root) {
  const result = [];
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    const fullPath = path.join(root, entry.name);
    if (entry.isDirectory()) {
      result.push(...walk(fullPath));
    } else if (entry.isFile()) {
      result.push(fullPath);
    }
  }
  return result;
}

function isDownload(file) {
  const name = path.basename(file).toLowerCase();
  return name.endsWith(".exe") || name.endsWith(".dmg") || name.endsWith(".tar.gz") || name.endsWith(".appimage");
}

function loadManifest(file) {
  if (!existsSync(file)) {
    return { schema: 1, versions: [] };
  }

  try {
    const parsed = JSON.parse(readFileSync(file, "utf8"));
    return {
      schema: 1,
      versions: Array.isArray(parsed.versions) ? parsed.versions : [],
    };
  } catch {
    return { schema: 1, versions: [] };
  }
}

function inferVersion(filesToInspect) {
  for (const file of filesToInspect) {
    const name = path.basename(file);
    const match =
      name.match(/^openhpsdr-zeus-(.+)-win-(?:x64|arm64)-setup\.exe$/i) ||
      name.match(/^openhpsdr-zeus-(.+)-linux-(?:x64|arm64)\.tar\.gz$/i) ||
      name.match(/^OpenhpsdrZeus-(.+)-macos-arm64\.dmg$/) ||
      name.match(/^OpenhpsdrZeus(?:-Server)?-(.+)-linux-(?:x86_64|aarch64)\.AppImage$/);
    if (match) return match[1];
  }
  return null;
}

function assetFor(file, versionValue, urlBase) {
  const filename = path.basename(file);
  const stats = statSync(file);
  const sha256 = createHash("sha256").update(readFileSync(file)).digest("hex");
  const common = {
    filename,
    url: `${urlBase}/versions/${encodeURIComponent(versionValue)}/${encodeURIComponent(filename)}`,
    size: stats.size,
    sha256,
  };

  let match = filename.match(/^openhpsdr-zeus-(.+)-win-(x64|arm64)-setup\.exe$/i);
  if (match) {
    return {
      ...common,
      platform: "windows",
      arch: match[2],
      kind: "installer",
      label: `Windows ${match[2].toUpperCase()} installer`,
    };
  }

  match = filename.match(/^OpenhpsdrZeus-(.+)-macos-(arm64)\.dmg$/);
  if (match) {
    return {
      ...common,
      platform: "macos",
      arch: match[2],
      kind: "dmg",
      label: "macOS Apple Silicon DMG",
    };
  }

  match = filename.match(/^openhpsdr-zeus-(.+)-linux-(x64|arm64)\.tar\.gz$/i);
  if (match) {
    return {
      ...common,
      platform: "linux",
      arch: match[2],
      kind: "tarball",
      label: `Linux ${match[2]} tarball`,
    };
  }

  match = filename.match(/^OpenhpsdrZeus(-Server)?-(.+)-linux-(x86_64|aarch64)\.AppImage$/);
  if (match) {
    const arch = match[3] === "x86_64" ? "x64" : "arm64";
    const mode = match[1] ? "server" : "desktop";
    return {
      ...common,
      platform: "linux",
      arch,
      kind: "appimage",
      mode,
      label: `Linux ${arch} ${mode} AppImage`,
    };
  }

  return null;
}

function assetSort(a, b) {
  const platformOrder = { windows: 0, macos: 1, linux: 2 };
  const archOrder = { x64: 0, arm64: 1 };
  const kindOrder = { installer: 0, dmg: 0, appimage: 1, tarball: 2 };
  return (
    (platformOrder[a.platform] ?? 9) - (platformOrder[b.platform] ?? 9) ||
    (archOrder[a.arch] ?? 9) - (archOrder[b.arch] ?? 9) ||
    (kindOrder[a.kind] ?? 9) - (kindOrder[b.kind] ?? 9) ||
    a.filename.localeCompare(b.filename)
  );
}

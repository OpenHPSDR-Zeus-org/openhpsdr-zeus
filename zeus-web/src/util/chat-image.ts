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

// Client-side photo compression for ZeusChat inline attachments.
//
// Photos are sent "like a text message": the bytes ride inside the chat message
// as a base64 data URL, which the relay persists in a single Durable-Object
// value (128 KiB cap) and broadcasts to the whole room. A raw phone photo is
// 2–8 MB — far too big — so before sending we downscale and JPEG-compress in the
// browser until the encoded data URL fits comfortably under the cap.
//
// The strategy: cap the longest edge, then step quality (and, if needed, the
// dimension) down until the result fits. This keeps the common case (a snapshot
// of a rig / antenna / screen) legible while guaranteeing the wire size.

import { MAX_ATTACHMENT_DATAURL_LEN, type ChatAttachment } from '../api/chat';

/** Longest-edge starting cap (px). Most chat photos read fine at this size. */
const MAX_EDGE_PX = 1280;
/** Hard floor for the longest edge when shrinking to fit (px). */
const MIN_EDGE_PX = 480;
/** JPEG quality ladder, highest first. */
const QUALITY_STEPS = [0.82, 0.72, 0.62, 0.5, 0.4];

/** Accepted input types. HEIC isn't decodable by canvas in most browsers. */
const ACCEPTED = /^image\/(jpeg|png|webp|gif|bmp)$/i;

export class ChatImageError extends Error {}

/** Human-facing accept attribute for the file picker. */
export const CHAT_IMAGE_ACCEPT = 'image/jpeg,image/png,image/webp,image/gif,image/bmp';

/** Rough decoded byte size of a base64 data URL (for the `size` hint). */
function dataUrlBytes(dataUrl: string): number {
  const comma = dataUrl.indexOf(',');
  const b64 = comma >= 0 ? dataUrl.slice(comma + 1) : dataUrl;
  return Math.floor((b64.length * 3) / 4);
}

/** Load a File into an HTMLImageElement via an object URL (revoked after). */
function loadImage(file: File): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const url = URL.createObjectURL(file);
    const img = new Image();
    img.onload = () => {
      URL.revokeObjectURL(url);
      resolve(img);
    };
    img.onerror = () => {
      URL.revokeObjectURL(url);
      reject(new ChatImageError("Couldn't read that image."));
    };
    img.src = url;
  });
}

/** Draw `img` to a canvas scaled so its longest edge is `edge` px. */
function drawScaled(img: HTMLImageElement, edge: number): HTMLCanvasElement {
  const w = img.naturalWidth || img.width;
  const h = img.naturalHeight || img.height;
  const scale = Math.min(1, edge / Math.max(w, h));
  const cw = Math.max(1, Math.round(w * scale));
  const ch = Math.max(1, Math.round(h * scale));
  const canvas = document.createElement('canvas');
  canvas.width = cw;
  canvas.height = ch;
  const ctx = canvas.getContext('2d');
  if (!ctx) throw new ChatImageError('Image processing is unavailable in this browser.');
  // White matte so transparent PNGs don't turn black when flattened to JPEG.
  ctx.fillStyle = '#ffffff';
  ctx.fillRect(0, 0, cw, ch);
  ctx.drawImage(img, 0, 0, cw, ch);
  return canvas;
}

/**
 * Compress `file` into an inline image attachment whose data URL fits under the
 * relay cap. Always re-encodes as JPEG (photos compress well; alpha is matted
 * onto white). Throws {@link ChatImageError} for an unreadable/unsupported file
 * or an image that can't be made small enough even at the floor settings.
 */
export async function compressImageToAttachment(file: File): Promise<ChatAttachment> {
  if (!file.type || !ACCEPTED.test(file.type)) {
    throw new ChatImageError('Only photos (JPEG, PNG, WebP, GIF) can be attached.');
  }

  const img = await loadImage(file);

  // Walk the edge sizes from MAX down to MIN; at each, try the quality ladder.
  // The first encoding under the cap wins. Larger edge + higher quality is
  // preferred, so we only shrink the edge once a size's whole quality ladder
  // overflows.
  for (let edge = MAX_EDGE_PX; edge >= MIN_EDGE_PX; edge = Math.round(edge * 0.8)) {
    const canvas = drawScaled(img, edge);
    for (const q of QUALITY_STEPS) {
      const dataUrl = canvas.toDataURL('image/jpeg', q);
      if (dataUrl.length <= MAX_ATTACHMENT_DATAURL_LEN) {
        return {
          kind: 'image',
          mime: 'image/jpeg',
          dataUrl,
          name: renameToJpeg(file.name),
          width: canvas.width,
          height: canvas.height,
          size: dataUrlBytes(dataUrl),
        };
      }
    }
  }

  throw new ChatImageError(
    "That image is too detailed to send inline. Try a smaller crop or screenshot.",
  );
}

/** Swap any extension for .jpg since we always re-encode as JPEG. */
function renameToJpeg(name: string | undefined): string | null {
  if (!name) return null;
  const base = name.replace(/\.[^./\\]+$/, '');
  return `${base || 'photo'}.jpg`;
}

// SPDX-License-Identifier: GPL-2.0-or-later

// Client for the "Reset & Uninstall Zeus" flow. The backend owns the entire
// footprint and the safety validation; the client only previews, triggers the
// one-click backup download, and confirms with the one-shot token.

export interface UninstallPreview {
  paths: string[];
  warnings: string[];
  abortReasons: string[];
  canProceed: boolean;
  binaryRemovalSupported: boolean;
  confirmToken: string;
}

export async function getUninstallPreview(removeBinary: boolean): Promise<UninstallPreview> {
  const res = await fetch(`/api/app/uninstall/preview?removeBinary=${removeBinary ? 'true' : 'false'}`);
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return (await res.json()) as UninstallPreview;
}

// Trigger the backup zip download (prefs + profiles + logbook + ADIF). The
// browser saves it to its Downloads folder — which the wipe never touches.
export function downloadBackup(): void {
  const a = document.createElement('a');
  a.href = '/api/app/backup';
  a.download = 'zeus-backup.zip';
  document.body.appendChild(a);
  a.click();
  a.remove();
}

export async function executeUninstall(token: string, removeBinary: boolean): Promise<void> {
  const res = await fetch('/api/app/uninstall', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token, removeBinary }),
  });
  if (!res.ok) {
    let msg = `HTTP ${res.status}`;
    try {
      const body = await res.json();
      if (body?.error) msg = body.error as string;
    } catch {
      /* ignore */
    }
    throw new Error(msg);
  }
}

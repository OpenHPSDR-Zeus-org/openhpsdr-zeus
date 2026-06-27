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

import { create } from 'zustand';

// A single, app-global "show this callsign's QRZ profile" modal. Lifting it out
// of ChatPanel (where it was local state) lets any surface — the chat roster, a
// message bubble, the panadapter chat-roster overlay — open the same card by
// calling open(callsign), without each one re-implementing the QRZ lookup +
// modal chrome. The host (<ProfileOverlayHost>) is mounted once at the app root.
export type ProfileOverlayState = {
  /** The callsign whose QRZ card is open, or null when nothing is shown. */
  callsign: string | null;
  /** Open the profile card for a callsign (normalised to upper-case). */
  open: (callsign: string) => void;
  /** Close the card. */
  close: () => void;
};

export const useProfileOverlayStore = create<ProfileOverlayState>((set) => ({
  callsign: null,
  open: (callsign) => {
    const c = callsign.trim().toUpperCase();
    if (c) set({ callsign: c });
  },
  close: () => set({ callsign: null }),
}));

/** Imperative helper for non-React callers / brevity at call sites. */
export function openProfileCard(callsign: string): void {
  useProfileOverlayStore.getState().open(callsign);
}

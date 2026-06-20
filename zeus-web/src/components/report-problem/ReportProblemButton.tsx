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

// Footer "Report a problem" button — opens the self-diagnostic modal. Lives in
// the bottom transport bar; matches the bar's ghost-button styling.

import { useReportProblemStore } from '../../state/report-problem-store';

export default function ReportProblemButton() {
  const open = useReportProblemStore((s) => s.open);

  return (
    <button
      type="button"
      className="btn ghost"
      aria-label="Report a problem with Zeus"
      title="Report a problem"
      onClick={open}
    >
      <span aria-hidden="true" style={{ marginRight: 6 }}>
        ⚠
      </span>
      Report a problem
    </button>
  );
}

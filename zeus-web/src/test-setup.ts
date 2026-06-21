// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Vitest setup. Tells React 19 that this is an act(...) environment so
// component tests using React's bundled `act` don't log noisy warnings.

// React 19 wants the host environment to advertise act-readiness via this
// magic global. Avoid `declare global { var ... }` (lint complains about
// implicit `var` redeclaration) and reach through `globalThis` directly.
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

export {};

// SPDX-License-Identifier: GPL-2.0-or-later
//
// react/jsx-runtime shim — see ./react.js for rationale. Some plugin
// builds emit `import { jsx as _jsx } from 'react/jsx-runtime'` instead
// of inlining the runtime. Map to host's runtime so JSX produces
// elements compatible with the host's React reconciler.

const JR = window.__zeus?.ReactJsxRuntime;
if (!JR) {
  throw new Error('zeus-sdk/react-jsx-runtime: window.__zeus.ReactJsxRuntime not initialised');
}

export const jsx = JR.jsx;
export const jsxs = JR.jsxs;
export const jsxDEV = JR.jsxDEV;
export const Fragment = JR.Fragment;

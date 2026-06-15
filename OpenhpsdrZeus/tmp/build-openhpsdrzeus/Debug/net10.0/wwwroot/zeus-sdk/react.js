// SPDX-License-Identifier: GPL-2.0-or-later
//
// React shim for plugin ES modules. The plugin's bundle imports `react`
// as a bare specifier; the browser can't resolve that at runtime, so
// the host index.html declares an import map mapping `react` to this
// file. Re-exports from window.__zeus.React (set in main.tsx) so plugin
// and host share a single React instance — required for hooks to work
// (ReactCurrentDispatcher is module-local).

const R = window.__zeus?.React;
if (!R) {
  throw new Error('zeus-sdk/react: window.__zeus.React not initialised yet — plugin loaded before host?');
}

export default R;

export const Children = R.Children;
export const Component = R.Component;
export const Fragment = R.Fragment;
export const Profiler = R.Profiler;
export const PureComponent = R.PureComponent;
export const StrictMode = R.StrictMode;
export const Suspense = R.Suspense;
export const cloneElement = R.cloneElement;
export const createContext = R.createContext;
export const createElement = R.createElement;
export const createRef = R.createRef;
export const forwardRef = R.forwardRef;
export const isValidElement = R.isValidElement;
export const lazy = R.lazy;
export const memo = R.memo;
export const startTransition = R.startTransition;
export const useCallback = R.useCallback;
export const useContext = R.useContext;
export const useDebugValue = R.useDebugValue;
export const useDeferredValue = R.useDeferredValue;
export const useEffect = R.useEffect;
export const useId = R.useId;
export const useImperativeHandle = R.useImperativeHandle;
export const useInsertionEffect = R.useInsertionEffect;
export const useLayoutEffect = R.useLayoutEffect;
export const useMemo = R.useMemo;
export const useReducer = R.useReducer;
export const useRef = R.useRef;
export const useState = R.useState;
export const useSyncExternalStore = R.useSyncExternalStore;
export const useTransition = R.useTransition;
export const version = R.version;

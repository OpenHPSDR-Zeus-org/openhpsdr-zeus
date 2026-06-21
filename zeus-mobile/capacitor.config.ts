// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
// See LICENSE / ATTRIBUTIONS.md at the repository root.

import type { CapacitorConfig } from '@capacitor/cli';

const config: CapacitorConfig = {
  appId: 'org.openhpsdr.zeus',
  appName: 'OpenHPSDR Zeus',
  // The Capacitor shell ships zeus-web's compiled output. zeus-web's vite
  // config points its build at Zeus.Server.Hosting/wwwroot/ (so the .NET
  // server can serve it directly); we re-use that same artefact here. The
  // user picks the LAN-side Zeus.Server at runtime via Settings → Server.
  // See ../zeus-web/src/serverUrl.ts.
  webDir: '../Zeus.Server.Hosting/wwwroot',
  bundledWebRuntime: false,
  android: {
    // Required for plain-HTTP requests to RFC1918 / link-local Zeus.Server
    // hosts. Combined with res/xml/network_security_config.xml so we don't
    // unconditionally allow cleartext to public hosts.
    allowMixedContent: true,
    backgroundColor: '#0a1220',
  },
  ios: {
    // The native shell loads the bundle from capacitor:// (default). Mic
    // permissions and ATS exceptions live in Info.plist via the apply-ios
    // patches script.
    contentInset: 'always',
    backgroundColor: '#0a1220',
  },
  server: {
    // Don't override URL here — that would force a fixed server at build time.
    // Operators configure the Zeus.Server endpoint at runtime instead.
    androidScheme: 'http',
    cleartext: true,
  },
};

export default config;

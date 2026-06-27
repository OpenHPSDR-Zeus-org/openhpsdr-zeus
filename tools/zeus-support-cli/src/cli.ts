#!/usr/bin/env -S npx tsx
/**
 * zeus-support — non-interactive admin CLI for the Zeus remote-diagnostics
 * broker. Authenticate with an admin token, see which consented operators are
 * online, request a read-only diagnostics session, and pull a consenting
 * operator's logs + diagnostics over WebRTC.
 *
 * All secrets come from flags or env (ZEUS_ADMIN_TOKEN, ZEUS_REMOTE_BROKER_URL,
 * QRZ creds for `login`). Nothing is written to disk by default.
 */

import { CliError } from './config.js';
import { runLogin } from './commands/login.js';
import { runToken } from './commands/token.js';
import { runPresence } from './commands/presence.js';
import { runRequest } from './commands/request.js';
import { runPull } from './commands/pull.js';

const TOP_HELP = `
zeus-support — Zeus remote-diagnostics admin CLI

Usage:
  zeus-support <command> [options]

Commands:
  login                 authenticate (QRZ + password); print a session token (--mint for agent token)
  token mint            exchange a session token for a long-lived agent token
  presence | list | whoami   list online, support-available operators
  request <callsign>    ask an operator to Allow a read-only diagnostics session
  pull <callsign>       request + connect + collect a diagnostics report (--mock for offline shaping)

Global env:
  ZEUS_ADMIN_TOKEN          Bearer token for protected commands
  ZEUS_REMOTE_BROKER_URL    broker base url (default https://remote.openhpsdrzeus.com)

Run "zeus-support <command> --help" for command-specific options.
`;

async function main(argv: string[]): Promise<number> {
  const [command, ...rest] = argv;

  switch (command) {
    case 'login':
      return runLogin(rest);
    case 'token':
      return runToken(rest);
    case 'presence':
    case 'list':
    case 'whoami':
      return runPresence(rest);
    case 'request':
      return runRequest(rest);
    case 'pull':
      return runPull(rest);
    case undefined:
    case 'help':
    case '-h':
    case '--help':
      process.stdout.write(`${TOP_HELP}\n`);
      return command === undefined ? 2 : 0;
    default:
      process.stderr.write(`unknown command: ${command}\n${TOP_HELP}\n`);
      return 2;
  }
}

main(process.argv.slice(2))
  .then((code) => {
    process.exitCode = code;
  })
  .catch((err: unknown) => {
    if (err instanceof CliError) {
      process.stderr.write(`error: ${err.message}\n`);
      process.exitCode = err.code;
    } else {
      process.stderr.write(`unexpected error: ${(err as Error)?.stack ?? String(err)}\n`);
      process.exitCode = 1;
    }
  });

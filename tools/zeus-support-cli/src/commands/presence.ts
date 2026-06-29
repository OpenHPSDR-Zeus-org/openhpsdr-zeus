/**
 * `presence` (aliases: `list`, `whoami`) — print the operators currently online
 * and support-available, from GET /admin/presence (Bearer ZEUS_ADMIN_TOKEN).
 */

import { parse, commonOptions } from '../args.js';
import { resolveBrokerUrl, requireAdminToken } from '../config.js';
import { BrokerClient } from '../broker.js';

export const presenceHelp = `
zeus-support presence — list online, support-available operators

Usage:
  ZEUS_ADMIN_TOKEN=<token> zeus-support presence

Options:
  --token <T>     Bearer token (or env ZEUS_ADMIN_TOKEN)
  --broker <URL>  broker base url (or env ZEUS_REMOTE_BROKER_URL)
  --json          print machine-readable JSON
`;

export async function runPresence(argv: string[]): Promise<number> {
  const { values } = parse(argv, commonOptions);
  if (values.help) {
    process.stdout.write(`${presenceHelp}\n`);
    return 0;
  }

  const baseUrl = resolveBrokerUrl(str(values.broker));
  const token = requireAdminToken(str(values.token));
  const client = new BrokerClient({ baseUrl, token });

  const { operators } = await client.presence();

  if (values.json) {
    process.stdout.write(`${JSON.stringify({ operators }, null, 2)}\n`);
    return 0;
  }

  if (operators.length === 0) {
    process.stdout.write('No operators are online / support-available.\n');
    return 0;
  }
  process.stdout.write(`Online operators (${operators.length}):\n`);
  for (const op of operators) {
    const since = fmt(op.since);
    const seen = fmt(op.lastSeen);
    process.stdout.write(`  ${op.callsign.padEnd(10)} since ${since}  last-seen ${seen}\n`);
  }
  return 0;
}

function fmt(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return '?';
  return new Date(ms).toISOString().replace('T', ' ').replace(/\.\d+Z$/, 'Z');
}

function str(v: string | boolean | undefined): string {
  return typeof v === 'string' ? v : '';
}

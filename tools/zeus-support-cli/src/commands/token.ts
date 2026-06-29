/**
 * `token mint` — exchange the current Bearer token (ZEUS_ADMIN_TOKEN, typically
 * a session token from `login`) for a long-lived agent token. The agent token is
 * shown ONCE; store it as ZEUS_ADMIN_TOKEN for non-interactive use.
 */

import { parse, commonOptions } from '../args.js';
import { resolveBrokerUrl, requireAdminToken, CliError } from '../config.js';
import { BrokerClient } from '../broker.js';

export const tokenHelp = `
zeus-support token mint — mint a long-lived agent token

Usage:
  ZEUS_ADMIN_TOKEN=<session-token> zeus-support token mint [--label agent]

Options:
  --label <L>     token label (default: agent)
  --token <T>     Bearer token (or env ZEUS_ADMIN_TOKEN)
  --broker <URL>  broker base url (or env ZEUS_REMOTE_BROKER_URL)
  --json          print machine-readable JSON
`;

export async function runToken(argv: string[]): Promise<number> {
  const sub = argv[0];
  const rest = argv.slice(1);

  const { values } = parse(rest, {
    ...commonOptions,
    label: { type: 'string' },
  });

  if (values.help || !sub) {
    process.stdout.write(`${tokenHelp}\n`);
    return sub ? 0 : 2;
  }
  if (sub !== 'mint') {
    throw new CliError(`unknown token subcommand: ${sub} (expected: mint).`, 2);
  }

  const baseUrl = resolveBrokerUrl(str(values.broker));
  const token = requireAdminToken(str(values.token));
  const client = new BrokerClient({ baseUrl, token });

  const minted = await client.mintToken(str(values.label) || 'agent');

  if (values.json) {
    process.stdout.write(`${JSON.stringify(minted, null, 2)}\n`);
    return 0;
  }
  process.stdout.write(`Agent token (id ${minted.id}, shown once — store it now):\n  ${minted.token}\n`);
  process.stdout.write(`\n  export ZEUS_ADMIN_TOKEN=${minted.token}\n`);
  return 0;
}

function str(v: string | boolean | undefined): string {
  return typeof v === 'string' ? v : '';
}

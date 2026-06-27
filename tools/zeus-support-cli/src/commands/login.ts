/**
 * `login` — interactive-ish credential login. Proves callsign ownership (QRZ
 * session) + admin password, prints the ~12h session token. With `--mint` it
 * immediately exchanges that session token for a long-lived agent token (the
 * non-interactive path automated agents should use).
 *
 * Secrets are read from flags or env and NEVER written to disk.
 */

import { parse, commonOptions } from '../args.js';
import { resolveBrokerUrl, CliError, normCallsign } from '../config.js';
import { BrokerClient } from '../broker.js';

export const loginHelp = `
zeus-support login — authenticate and print an admin token

Usage:
  zeus-support login --callsign <CALL> [--qrz-session <SID>] [--password <PW>] [--mint]

Options:
  --callsign <CALL>     your admin callsign           (or env ZEUS_QRZ_CALLSIGN)
  --qrz-session <SID>   QRZ XML session key           (or env ZEUS_QRZ_SESSION)
  --password <PW>       your admin password           (or env ZEUS_ADMIN_PASSWORD)
  --mint                also mint a long-lived agent token (recommended for agents)
  --label <L>           label for the minted token    (default: agent)
  --broker <URL>        broker base url                (or env ZEUS_REMOTE_BROKER_URL)
  --json                print machine-readable JSON

Tip: export the printed token as ZEUS_ADMIN_TOKEN for the other commands.
`;

export async function runLogin(argv: string[]): Promise<number> {
  const { values } = parse(argv, {
    ...commonOptions,
    callsign: { type: 'string' },
    'qrz-session': { type: 'string' },
    password: { type: 'string' },
    mint: { type: 'boolean' },
    label: { type: 'string' },
  });

  if (values.help) {
    process.stdout.write(`${loginHelp}\n`);
    return 0;
  }

  const callsign = normCallsign(
    str(values.callsign) || process.env.ZEUS_QRZ_CALLSIGN || '',
  );
  const qrzSession = str(values['qrz-session']) || process.env.ZEUS_QRZ_SESSION || '';
  const password = str(values.password) || process.env.ZEUS_ADMIN_PASSWORD || '';

  if (!callsign) throw new CliError('missing --callsign (or ZEUS_QRZ_CALLSIGN).', 2);
  if (!qrzSession) throw new CliError('missing --qrz-session (or ZEUS_QRZ_SESSION).', 2);
  if (!password) throw new CliError('missing --password (or ZEUS_ADMIN_PASSWORD).', 2);

  const baseUrl = resolveBrokerUrl(str(values.broker));
  const client = new BrokerClient({ baseUrl });

  const session = await client.login({ qrzSession, callsign, password });

  let agentToken: { id: string; token: string } | undefined;
  if (values.mint) {
    const minted = new BrokerClient({ baseUrl, token: session.token });
    agentToken = await minted.mintToken(str(values.label) || 'agent');
  }

  if (values.json) {
    process.stdout.write(
      `${JSON.stringify(
        {
          callsign: session.callsign,
          sessionToken: session.token,
          expiresAt: session.expiresAt,
          agentToken: agentToken ?? null,
        },
        null,
        2,
      )}\n`,
    );
    return 0;
  }

  const expiry = new Date(session.expiresAt).toISOString();
  process.stdout.write(`Logged in as ${session.callsign}.\n`);
  process.stdout.write(`Session token (expires ${expiry}):\n  ${session.token}\n`);
  if (agentToken) {
    process.stdout.write(`\nAgent token (id ${agentToken.id}, shown once — store it now):\n  ${agentToken.token}\n`);
    process.stdout.write(`\nUse it with other commands:\n  export ZEUS_ADMIN_TOKEN=${agentToken.token}\n`);
  } else {
    process.stdout.write(`\nUse it with other commands:\n  export ZEUS_ADMIN_TOKEN=${session.token}\n`);
    process.stdout.write(`(or re-run with --mint for a non-expiring agent token)\n`);
  }
  return 0;
}

function str(v: string | boolean | undefined): string {
  return typeof v === 'string' ? v : '';
}

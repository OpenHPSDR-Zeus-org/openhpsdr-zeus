/**
 * `request <callsign>` — ask the operator to Allow a read-only diagnostics
 * session (POST /admin/request). Prints the requestId + single-use ticket and
 * explains that the operator must press Allow on their radio before the ticket
 * can be used with `pull`. 503 → "operator offline".
 */

import { parse, commonOptions } from '../args.js';
import { resolveBrokerUrl, requireAdminToken, normCallsign, CliError } from '../config.js';
import { BrokerClient } from '../broker.js';

export const requestHelp = `
zeus-support request <callsign> — request a diagnostics session

Usage:
  ZEUS_ADMIN_TOKEN=<token> zeus-support request <CALLSIGN>

Options:
  --token <T>     Bearer token (or env ZEUS_ADMIN_TOKEN)
  --broker <URL>  broker base url (or env ZEUS_REMOTE_BROKER_URL)
  --json          print machine-readable JSON
`;

export async function runRequest(argv: string[]): Promise<number> {
  const { values, positionals } = parse(argv, commonOptions);
  if (values.help) {
    process.stdout.write(`${requestHelp}\n`);
    return 0;
  }

  const callsign = normCallsign(positionals[0] ?? '');
  if (!callsign) throw new CliError('usage: zeus-support request <callsign>', 2);

  const baseUrl = resolveBrokerUrl(str(values.broker));
  const token = requireAdminToken(str(values.token));
  const client = new BrokerClient({ baseUrl, token });

  const res = await client.request(callsign);

  if (values.json) {
    process.stdout.write(`${JSON.stringify(res, null, 2)}\n`);
    return 0;
  }
  process.stdout.write(`Requested a diagnostics session with ${res.callsign}.\n`);
  process.stdout.write(`  requestId : ${res.requestId}\n`);
  process.stdout.write(`  ticket    : ${res.ticket}\n`);
  process.stdout.write(`\n${res.callsign} must press Allow on their radio. Then run:\n`);
  process.stdout.write(`  zeus-support pull ${res.callsign}\n`);
  return 0;
}

function str(v: string | boolean | undefined): string {
  return typeof v === 'string' ? v : '';
}

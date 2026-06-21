import type { Env } from './types';

/** Max TTL Cloudflare allows for minted TURN credentials (48 h). */
const TURN_TTL_SECONDS = 86_400; // 24 h — client re-mints before expiry

/**
 * Mint short-lived Cloudflare Realtime TURN credentials. The Worker holds the
 * TURN key as a secret; clients never see it. Returns a ready-to-use iceServers
 * array (STUN + TURN incl. a 443/TLS transport for restrictive firewalls).
 *
 * Throws if TURN is not configured (TURN_KEY_ID / TURN_API_TOKEN unset) or the
 * Cloudflare API rejects the request — the caller returns 503 and clients fall
 * back to STUN-only (direct P2P still works for most home networks).
 */
export async function mintIceServers(env: Env): Promise<unknown> {
  if (!env.TURN_KEY_ID || !env.TURN_API_TOKEN) {
    throw new Error('TURN not configured');
  }

  const res = await fetch(
    `https://rtc.live.cloudflare.com/v1/turn/keys/${env.TURN_KEY_ID}/credentials/generate-ice-servers`,
    {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${env.TURN_API_TOKEN}`,
        'content-type': 'application/json',
      },
      body: JSON.stringify({ ttl: TURN_TTL_SECONDS }),
    },
  );
  if (!res.ok) throw new Error(`cloudflare turn ${res.status}`);
  return await res.json();
}

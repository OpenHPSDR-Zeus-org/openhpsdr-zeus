/** TTL (seconds) for cached positive QRZ-session verdicts. */
const QRZ_VERIFY_TTL = 300;

/**
 * QRZ verification with a short-lived positive-result cache (edge Cache API).
 * Identical model to the chat relay: only valid verdicts are cached (never
 * negative), so a freshly-logged-in operator is never locked out, and legit
 * reconnects within the TTL don't re-hit QRZ. Fails closed.
 */
export async function verifyQrzSessionCached(
  sessionKey: string,
  callsign: string,
  ctx: ExecutionContext,
): Promise<boolean> {
  const digest = await sha256Hex(`${sessionKey}|${callsign}`);
  const cacheKey = new Request(`https://qrz-verify.zeus-remote.internal/${digest}`);
  const cache = caches.default;

  const hit = await cache.match(cacheKey);
  if (hit) return (await hit.text()) === '1';

  const ok = await verifyQrzSession(sessionKey, callsign);
  if (ok) {
    ctx.waitUntil(
      cache.put(
        cacheKey,
        new Response('1', { headers: { 'Cache-Control': `max-age=${QRZ_VERIFY_TTL}` } }),
      ),
    );
  }
  return ok;
}

/**
 * Validate a QRZ XML session key by looking up the operator's own callsign. A
 * live session returns a <Key>; an expired one omits it. Fails closed on any
 * QRZ/network error.
 */
export async function verifyQrzSession(sessionKey: string, callsign: string): Promise<boolean> {
  const u =
    'https://xmldata.qrz.com/xml/current/' +
    `?s=${encodeURIComponent(sessionKey)}` +
    `;callsign=${encodeURIComponent(callsign)}` +
    ';agent=zeus-remote-broker';
  try {
    const res = await fetch(u, { cf: { cacheTtl: 0 } });
    if (!res.ok) return false;
    const xml = await res.text();
    const hasKey = /<Key>\s*[^<\s][^<]*<\/Key>/i.test(xml);
    const errText = /<Error>([^<]*)<\/Error>/i.exec(xml)?.[1] ?? '';
    const badSession = /session timeout|invalid session|session expired|not logged/i.test(errText);
    return hasKey && !badSession;
  } catch {
    return false;
  }
}

async function sha256Hex(input: string): Promise<string> {
  const data = new TextEncoder().encode(input);
  const buf = await crypto.subtle.digest('SHA-256', data);
  return [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

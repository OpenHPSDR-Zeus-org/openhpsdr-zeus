// Minimal runtime smoke test for the ZeusChat relay.
// Connects, sends `hello` + a `msg`, collects relay responses, prints them.
// Usage: node test/smoke.mjs   (set CHAT_URL to override the endpoint)
const url = process.env.CHAT_URL || 'ws://127.0.0.1:8787/chat';
const token = process.env.RELAY_TOKEN;
const target = token ? `${url}?token=${encodeURIComponent(token)}` : url;

if (typeof WebSocket === 'undefined') {
  console.error('FAIL: global WebSocket unavailable (need Node >= 21)');
  process.exit(3);
}

const got = [];
const ws = new WebSocket(target);

ws.addEventListener('open', () => {
  ws.send(JSON.stringify({ t: 'hello', callsign: 'w1abc', freq: 14074000, mode: 'FT8' }));
  setTimeout(() => ws.send(JSON.stringify({ t: 'msg', text: 'hello world' })), 300);
  setTimeout(() => {
    const types = got.map((g) => { try { return JSON.parse(g).t; } catch { return '?'; } });
    console.log('RECEIVED TYPES:', types.join(', '));
    console.log('FRAMES:');
    for (const g of got) console.log('  ' + g);
    const ok = types.includes('welcome') && types.includes('roster') && types.includes('msg');
    console.log(ok ? 'SMOKE: PASS' : 'SMOKE: FAIL');
    ws.close();
    process.exit(ok ? 0 : 1);
  }, 900);
});

ws.addEventListener('message', (e) => got.push(typeof e.data === 'string' ? e.data : ''));
ws.addEventListener('error', (e) => {
  console.error('WS ERROR:', e?.message || String(e));
  process.exit(2);
});

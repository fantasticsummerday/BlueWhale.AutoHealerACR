// Scrape a Tencent Doc page through the Chrome DevTools Protocol.
// Collects absolutely-positioned text nodes while auto-scrolling the document.
const fs = require('fs');
const http = require('http');

const PORT = 9333;
const OUT = process.argv[2] || 'scraped.txt';
const ROUNDS = Number(process.argv[3] || 45);

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function httpGet(path) {
  return new Promise((resolve, reject) => {
    const req = http.request({ host: '127.0.0.1', port: PORT, path, method: 'GET' }, (r) => {
      let b = '';
      r.setEncoding('utf8');
      r.on('data', (d) => (b += d));
      r.on('end', () => resolve(b));
    });
    req.on('error', reject);
    req.end();
  });
}

const COLLECT = `(() => {
  const out = [];
  const w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  let n;
  while ((n = w.nextNode())) {
    const t = (n.nodeValue || '').replace(/\\s+/g, ' ').trim();
    if (!t) continue;
    const el = n.parentElement;
    if (!el || el.closest('script,style,noscript,svg')) continue;
    const r = el.getBoundingClientRect();
    if (r.width === 0 && r.height === 0) continue;
    out.push([t, Math.round(r.top), Math.round(r.left)]);
  }
  return JSON.stringify(out);
})()`;

const SCROLL = `(() => {
  const cands = [document.scrollingElement, ...document.querySelectorAll('div,section,main')];
  let moved = 0;
  for (const e of cands) {
    if (!e) continue;
    const can = e.scrollHeight - e.clientHeight;
    if (can > 40 && e.clientHeight > 200) {
      const before = e.scrollTop;
      e.scrollTop = Math.min(e.scrollTop + e.clientHeight * 0.85, e.scrollHeight);
      if (e.scrollTop !== before) moved++;
    }
  }
  return moved;
})()`;

function connect(wsUrl) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl);
    const pending = new Map();
    let id = 0;
    ws.addEventListener('open', () =>
      resolve({
        send(method, params = {}) {
          return new Promise((res, rej) => {
            const mid = ++id;
            pending.set(mid, { res, rej });
            ws.send(JSON.stringify({ id: mid, method, params }));
            setTimeout(() => {
              if (pending.has(mid)) {
                pending.delete(mid);
                rej(new Error('timeout ' + method));
              }
            }, 30000);
          });
        },
        close: () => ws.close(),
      })
    );
    ws.addEventListener('message', (ev) => {
      let msg;
      try {
        msg = JSON.parse(ev.data);
      } catch {
        return;
      }
      if (msg.id && pending.has(msg.id)) {
        const { res, rej } = pending.get(msg.id);
        pending.delete(msg.id);
        if (msg.error) rej(new Error(JSON.stringify(msg.error)));
        else res(msg.result);
      }
    });
    ws.addEventListener('error', (e) => reject(new Error('ws error ' + (e.message || ''))));
  });
}

(async () => {
  let list = [];
  for (let i = 0; i < 30; i++) {
    try {
      list = JSON.parse(await httpGet('/json/list'));
      if (list.some((t) => t.type === 'page' && /docs\.qq\.com/.test(t.url || ''))) break;
    } catch {}
    await sleep(1000);
  }
  const page = list.find((t) => t.type === 'page' && /docs\.qq\.com/.test(t.url || '')) || list.find((t) => t.type === 'page');
  if (!page) {
    console.error('no page target; targets=' + JSON.stringify(list.map((t) => t.type + ':' + t.url)));
    process.exit(2);
  }
  const cdp = await connect(page.webSocketDebuggerUrl);
  await cdp.send('Runtime.enable');

  const evalJs = async (expr) => {
    const r = await cdp.send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: false });
    if (r.exceptionDetails) throw new Error(JSON.stringify(r.exceptionDetails).slice(0, 300));
    return r.result.value;
  };

  // wait for the editor to render
  for (let i = 0; i < 30; i++) {
    await sleep(1000);
    const ready = await evalJs(`document.body ? document.body.innerText.length : 0`).catch(() => 0);
    if (ready > 100) break;
  }

  const seen = new Map(); // text -> {y, x, order}
  let order = 0;
  let stagnant = 0;

  for (let round = 0; round < ROUNDS; round++) {
    let raw;
    try {
      raw = await evalJs(COLLECT);
    } catch (e) {
      console.error('collect failed: ' + e.message);
      break;
    }
    const items = JSON.parse(raw || '[]');
    let added = 0;
    for (const [t, top, left] of items) {
      if (!seen.has(t)) {
        seen.set(t, { y: top, x: left, order: order++ });
        added++;
      }
    }
    const moved = await evalJs(SCROLL).catch(() => 0);
    if (added === 0) stagnant++;
    else stagnant = 0;
    if (round % 5 === 0) console.error(`round ${round}: items=${items.length} new=${added} total=${seen.size} moved=${moved}`);
    if (stagnant >= 6 && moved === 0) break;
    await sleep(700);
  }

  const rows = [...seen.entries()].map(([t, m]) => ({ t, ...m }));
  rows.sort((a, b) => a.y - b.y || a.x - b.x || a.order - b.order);
  fs.writeFileSync(OUT, rows.map((r) => r.t).join('\n'), 'utf8');
  console.error('total unique nodes: ' + rows.length);
  cdp.close();
  process.exit(0);
})().catch((e) => {
  console.error('FATAL ' + e.stack);
  process.exit(1);
});

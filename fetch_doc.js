// Pull the full document JSON out of the page context (same-origin fetch with
// the visitor's cookies) and write it to disk in chunks.
const fs = require('fs');
const http = require('http');

const PORT = 9333;
const CHUNK = 120000;

function httpGet(path) {
  return new Promise((resolve, reject) => {
    const req = http.request({ host: '127.0.0.1', port: PORT, path, method: 'GET' }, (r) => {
      let b = ''; r.setEncoding('utf8'); r.on('data', (d) => (b += d)); r.on('end', () => resolve(b));
    });
    req.on('error', reject); req.end();
  });
}

const START = `(async () => {
  const r = await fetch('/dop-api/opendoc?id=DVVl4SVJMU3V5d1Na&normal=1&outformat=1&noEscape=1', { credentials: 'include' });
  window.__dshDoc = await r.text();
  return window.__dshDoc.length;
})()`;

function connect(wsUrl) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl);
    const pending = new Map();
    let id = 0;
    ws.addEventListener('open', () => resolve({
      send(method, params = {}) {
        return new Promise((res, rej) => {
          const mid = ++id;
          pending.set(mid, { res, rej });
          ws.send(JSON.stringify({ id: mid, method, params }));
          setTimeout(() => { if (pending.has(mid)) { pending.delete(mid); rej(new Error('timeout ' + method)); } }, 60000);
        });
      },
      close: () => ws.close(),
    }));
    ws.addEventListener('message', (ev) => {
      let msg; try { msg = JSON.parse(ev.data); } catch { return; }
      if (msg.id && pending.has(msg.id)) {
        const { res, rej } = pending.get(msg.id);
        pending.delete(msg.id);
        if (msg.error) rej(new Error(JSON.stringify(msg.error))); else res(msg.result);
      }
    });
    ws.addEventListener('error', (e) => reject(new Error('ws ' + (e.message || ''))));
  });
}

(async () => {
  const list = JSON.parse(await httpGet('/json/list'));
  const page = list.find((t) => t.type === 'page' && /docs\.qq\.com/.test(t.url || ''));
  if (!page) { console.error('no doc page'); process.exit(2); }
  const cdp = await connect(page.webSocketDebuggerUrl);
  await cdp.send('Runtime.enable');

  const evalJs = async (expression, awaitPromise = false) => {
    const r = await cdp.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise });
    if (r.exceptionDetails) throw new Error(JSON.stringify(r.exceptionDetails).slice(0, 400));
    return r.result.value;
  };

  const len = await evalJs(START, true);
  console.error('doc json length: ' + len);

  const parts = [];
  for (let off = 0; off < len; off += CHUNK) {
    const piece = await evalJs(`window.__dshDoc.substr(${off}, ${CHUNK})`);
    parts.push(piece);
  }
  fs.writeFileSync('doc.json', parts.join(''), 'utf8');
  console.error('saved doc.json, chars=' + parts.join('').length);
  cdp.close();
  process.exit(0);
})().catch((e) => { console.error('FATAL ' + e.stack); process.exit(1); });

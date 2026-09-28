// Probe the open doc page: what is actually rendered, and can the in-page
// fetch reach the opendoc API with the visitor's own cookies?
const fs = require('fs');
const http = require('http');

const PORT = 9333;
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

const PROBE = `(async () => {
  const res = {};
  try { res.url = location.href; } catch(e) {}
  res.innerTextLen = document.body ? document.body.innerText.length : -1;
  res.canvas = document.querySelectorAll('canvas').length;
  res.iframes = [...document.querySelectorAll('iframe')].map(f => f.src || '(srcdoc)');
  res.text = document.body ? document.body.innerText.replace(/\\n{2,}/g,'\\n').slice(0, 2000) : '';
  res.cookies = document.cookie.slice(0, 500);
  const urls = [
    '/dop-api/opendoc?id=DVVl4SVJMU3V5d1Na&normal=1&outformat=1&noEscape=1',
    '/dop-api/opendoc?id=DVVl4SVJMU3V5d1Na&normal=1&outformat=1&noEscape=1&startrow=0&endrow=200',
    '/dop-api/opendoc?id=DVVl4SVJMU3V5d1Na&type=doc&normal=1'
  ];
  res.api = [];
  for (const u of urls) {
    try {
      const r = await fetch(u, { credentials: 'include' });
      const t = await r.text();
      res.api.push({ u, status: r.status, len: t.length, head: t.slice(0, 300) });
    } catch (e) {
      res.api.push({ u, err: String(e).slice(0, 200) });
    }
  }
  return JSON.stringify(res);
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
              if (pending.has(mid)) { pending.delete(mid); rej(new Error('timeout ' + method)); }
            }, 40000);
          });
        },
        close: () => ws.close(),
      })
    );
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
  if (!page) { console.error('no doc page: ' + JSON.stringify(list.map(t => t.url))); process.exit(2); }
  const cdp = await connect(page.webSocketDebuggerUrl);
  await cdp.send('Runtime.enable');
  const r = await cdp.send('Runtime.evaluate', { expression: PROBE, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) { console.error('EXC ' + JSON.stringify(r.exceptionDetails).slice(0, 500)); process.exit(3); }
  fs.writeFileSync('probe.json', typeof r.result.value === 'string' ? r.result.value : JSON.stringify(r.result.value), 'utf8');
  console.error('probe written');
  cdp.close();
  process.exit(0);
})().catch((e) => { console.error('FATAL ' + e.stack); process.exit(1); });

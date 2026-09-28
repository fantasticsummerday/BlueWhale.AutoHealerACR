// 从腾讯文档导出 JSON 里提取纯文本
const fs = require('fs');

const IN = process.argv[2] || 'doc2.json';
const OUT = process.argv[3] || 'doc2_clean.txt';

const j = JSON.parse(fs.readFileSync(IN, 'utf8'));

const out = [];

function walk(node, depth) {
  if (node == null || depth > 14) return;

  if (typeof node === 'string') {
    return;
  }

  if (Array.isArray(node)) {
    for (const x of node) walk(x, depth + 1);
    return;
  }

  if (typeof node === 'object') {
    // 腾讯文档的文本常在 text / content / 或者是 {"text": "..."} 形式
    for (const k of Object.keys(node)) {
      const v = node[k];

      if (typeof v === 'string' && v.length > 0) {
        // 只收看起来像文档内容的（过滤掉路径、id 之类）
        if (/[\u4e00-\u9fa5]/.test(v) && !/^https?:/.test(v)) {
          out.push(v);
        }
      } else if (v && typeof v === 'object') {
        walk(v, depth + 1);
      }
    }
  }
}

walk(j, 0);

// 去重 + 合并
const seen = new Set();
const lines = [];
for (const s of out) {
  const t = s.replace(/\r/g, '').trim();
  if (!t) continue;
  if (seen.has(t)) continue;
  seen.add(t);
  lines.push(t);
}

// 如果内容里有换行，拆开
const final = [];
for (const l of lines) {
  for (const p of l.split('\n')) {
    const q = p.trim();
    if (q) final.push(q);
  }
}

fs.writeFileSync(OUT, final.join('\n'), 'utf8');
console.error('lines=' + final.length + ' chars=' + final.join('\n').length);
console.error('--- 前 40 行 ---');
console.error(final.slice(0, 40).join('\n'));

// 抓腾讯文档导出 API（带浏览器 UA 和 Referer）
const https = require('https');
const fs = require('fs');

const DOCID = process.argv[2] || 'DVVhLWmZiZ1RzYklZ';
const OUT = process.argv[3] || 'doc2.json';

const url = `https://docs.qq.com/dop-api/opendoc?id=${DOCID}&normal=1&outformat=1&noEscape=1`;

const opts = {
  headers: {
    'User-Agent':
      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36',
    Referer: `https://docs.qq.com/doc/${DOCID}`,
    Accept: '*/*',
    'Accept-Language': 'zh-CN,zh;q=0.9',
  },
};

https
  .get(url, opts, (r) => {
    console.error('HTTP ' + r.statusCode);
    let body = '';
    r.setEncoding('utf8');
    r.on('data', (d) => (body += d));
    r.on('end', () => {
      console.error('length=' + body.length);
      console.error('head=' + body.slice(0, 300));
      if (body.length > 1000) {
        fs.writeFileSync(OUT, body, 'utf8');
        console.error('written to ' + OUT);
      } else {
        console.error('too short, not written');
      }
    });
  })
  .on('error', (e) => console.error('ERR ' + e.message));

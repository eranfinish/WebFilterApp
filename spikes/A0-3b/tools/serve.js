// Spike A0.3b test server. Serves ../testpages on two ports of 127.0.0.1; the emulator reaches them as
// http://10.0.2.2:<port> (10.0.2.2 is the host loopback from the AVD). Port 8130 = the "blocked" origin, 8131 = the
// "allowed" origin. Every request is appended as one JSON line to evidence/serve-a03b.log, so the report can show
// exactly which requests reached this host (and with which query). Missing files answer 404 but are still logged.
// Run from spikes/A0-3b:  node tools/serve.js
const http = require('http');
const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..', 'testpages');
const logFile = path.join(__dirname, '..', 'evidence', 'serve-a03b.log');
const PORTS = [8130, 8131];
const types = { '.html': 'text/html; charset=utf-8', '.js': 'application/javascript', '.svg': 'image/svg+xml', '.png': 'image/png' };

function logLine(obj) {
  const line = JSON.stringify(obj);
  fs.appendFileSync(logFile, line + '\n');
  console.log(line);
}

for (const port of PORTS) {
  http.createServer((req, res) => {
    const u = new URL(req.url, 'http://x');
    logLine({
      ts: new Date().toISOString(), port, method: req.method, host: req.headers.host || '',
      path: u.pathname, query: u.search, referer: req.headers.referer || '',
      dest: req.headers['sec-fetch-dest'] || '', ua: (req.headers['user-agent'] || '').slice(0, 80),
    });
    const rel = decodeURIComponent(u.pathname);
    let p = path.join(root, rel);
    if (!p.startsWith(root) || !fs.existsSync(p) || fs.statSync(p).isDirectory()) {
      res.writeHead(404, { 'content-type': 'text/plain', 'cache-control': 'no-store' });
      res.end('not found');
      return;
    }
    res.writeHead(200, { 'content-type': types[path.extname(p)] || 'application/octet-stream', 'cache-control': 'no-store' });
    fs.createReadStream(p).pipe(res);
  }).listen(port, '127.0.0.1', () => console.log('serving', root, 'on 127.0.0.1:' + port));
}

// Spike A0.3 test server: serves ../testpages on 0.0.0.0:8000 (the emulator reaches it as http://10.0.2.2:8000).
// Logs every request line, so the report can show which requests Chrome actually sent to this host.
// Run: node tools\serve.js  (from spikes/A0-3)
const http = require('http');
const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..', 'testpages');
// PORT=8001 runs a second origin on the same host (Q3: a port-specific host rule blocks its subresources).
const PORT = Number(process.env.PORT || 8000);
const types = { '.html': 'text/html; charset=utf-8', '.js': 'application/javascript', '.png': 'image/png', '.svg': 'image/svg+xml', '.json': 'application/json' };

http.createServer((req, res) => {
  const u = new URL(req.url, 'http://x');
  console.log(new Date().toISOString(), req.socket.remoteAddress, req.method, req.headers.host, req.url, 'ua=' + (req.headers['user-agent'] || '').slice(0, 60));
  let p = path.join(root, decodeURIComponent(u.pathname));
  if (u.pathname.endsWith('/')) p = path.join(p, 'index.html');
  if (!p.startsWith(root) || !fs.existsSync(p) || fs.statSync(p).isDirectory()) {
    res.writeHead(404, { 'content-type': 'text/plain' });
    res.end('not found');
    return;
  }
  res.writeHead(200, { 'content-type': types[path.extname(p)] || 'application/octet-stream', 'cache-control': 'no-store' });
  fs.createReadStream(p).pipe(res);
}).listen(PORT, '0.0.0.0', () => console.log('serving', root, 'on :' + PORT));

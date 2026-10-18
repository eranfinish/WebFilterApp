// Local fixture server. With --host-resolver-rules every test host resolves to
// 127.0.0.1, so any hostname is served here and every request is recorded.
import { createServer } from "node:http";

const PIXEL = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=", "base64");
const esc = (s) => s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);

export function startServer(port = 8080) {
  const hits = [];
  const server = createServer((req, res) => {
    const url = new URL(req.url, `http://${req.headers.host}`);
    const host = url.hostname;
    hits.push({ host, path: url.pathname + url.search });
    const html = (title, body) => {
      res.writeHead(200, { "content-type": "text/html; charset=utf-8" });
      res.end(`<!doctype html><title>${esc(title)}</title><body>${body}</body>`);
    };
    const src = url.searchParams.get("src") ?? "";
    switch (url.pathname) {
      case "/pixel.png":
        res.writeHead(200, { "content-type": "image/png" });
        res.end(PIXEL);
        return;
      case "/with-iframe":
        return html(`SITE ${host}/with-iframe`, `<iframe id="f" src="${esc(src)}" width="400" height="200"></iframe>`);
      case "/with-img":
        return html(`SITE ${host}/with-img`, `<img id="i" src="${esc(src)}">`);
      case "/embed": {
        const v = encodeURIComponent(url.searchParams.get("v") ?? "");
        const nocookie = url.searchParams.has("nocookie");
        const origin = nocookie ? "https://www.youtube-nocookie.com" : "https://www.youtube.com";
        return html("EMBED", `<iframe id="yt" src="${origin}/embed/${v}" width="560" height="315" allow="autoplay; encrypted-media" allowfullscreen></iframe>`);
      }
      default:
        return html(`SITE ${host}${url.pathname}`, `<h1>${esc(host)}</h1>`);
    }
  });
  return new Promise((resolve) => server.listen(port, "127.0.0.1", () =>
    resolve({ hits, clearHits: () => { hits.length = 0; }, close: () => new Promise((r) => server.close(r)) })));
}

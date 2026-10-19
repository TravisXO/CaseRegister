// Serves the built site (dist) the way production does, for the browser checks (Playwright, Lighthouse): the headers of the first block of
// public/_headers on every response, files compressed on the way out (so what is measured is what is sent), and the single page every
// address that is not a file is answered with. Nothing here is used by the site itself.
import { createReadStream, existsSync, readFileSync, statSync } from "node:fs";
import { createServer } from "node:http";
import { extname, join, normalize, resolve, sep } from "node:path";
import { createGzip } from "node:zlib";

const positional = process.argv.slice(2).filter((argument) => !argument.startsWith("--"));
const dist = resolve(positional[0] ?? "dist");
const port = Number(process.env.PORT ?? positional[1] ?? 4173);

const types = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8", ".svg": "image/svg+xml", ".json": "application/json", ".webmanifest": "application/manifest+json", ".png": "image/png", ".ico": "image/x-icon", ".txt": "text/plain; charset=utf-8", ".woff2": "font/woff2" };
const compressible = new Set([".html", ".js", ".css", ".svg", ".json", ".webmanifest", ".txt"]);

/** The headers every page gets (the first block of public/_headers), except the one that tells a browser to insist on https for the host. */
function pageHeaders() {
  const headers = {};
  let blocks = 0;
  for (const line of readFileSync(resolve("public/_headers"), "utf8").split("\n")) {
    if (line.startsWith("/")) {
      blocks += 1;
    }

    const match = blocks === 1 ? /^\s+([A-Za-z-]+):\s*(.+?)\s*$/.exec(line) : null;
    if (match?.[1] && match[2] && match[1] !== "Strict-Transport-Security") {
      headers[match[1]] = match[2];
    }
  }

  return headers;
}

const base = pageHeaders();

function locate(urlPath) {
  const decoded = decodeURIComponent(urlPath.split("?")[0] ?? "/");
  const candidate = normalize(join(dist, decoded));
  if (candidate !== dist && !candidate.startsWith(dist + sep)) {
    return null; // never above the folder
  }

  return existsSync(candidate) && statSync(candidate).isFile() ? candidate : null;
}

// With --stub-api the API does not exist (the audit loads public pages only): a call to it is answered as a signed-out visitor would be, and
// nothing else, so the page is audited as it is for someone who has just arrived.
const stubApi = process.argv.includes("--stub-api");

createServer((request, response) => {
  if (stubApi && (request.url ?? "").startsWith("/api/")) {
    response.writeHead(401, { ...base, "Content-Type": "application/problem+json" });
    response.end(JSON.stringify({ status: 401, title: "Unauthorized" }));

    return;
  }

  const file = locate(request.url ?? "/") ?? join(dist, "index.html");
  const extension = extname(file);
  const headers = { ...base, "Content-Type": new Map(Object.entries(types)).get(extension) ?? "application/octet-stream", Vary: "Accept-Encoding" };
  if (file.includes(`${sep}assets${sep}`)) {
    headers["Cache-Control"] = "public, max-age=31536000, immutable";
  }

  const wantsGzip = compressible.has(extension) && /\bgzip\b/.test(request.headers["accept-encoding"] ?? "");
  if (wantsGzip) {
    headers["Content-Encoding"] = "gzip";
  } else {
    headers["Content-Length"] = String(statSync(file).size);
  }

  response.writeHead(200, headers);
  const stream = createReadStream(file);
  (wantsGzip ? stream.pipe(createGzip()) : stream).pipe(response);
}).listen(port, () => {
  console.log(`Serving ${dist} on http://localhost:${String(port)}`);
});

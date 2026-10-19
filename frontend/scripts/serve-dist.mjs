// Serves the built site (dist) the way production does, for the browser checks (Playwright, Lighthouse): the headers of the first block of
// public/_headers on every response, files compressed on the way out (so what is measured is what is sent), and the single page every
// address that is not a file is answered with. Nothing here is used by the site itself.
import { createReadStream, readdirSync, readFileSync, statSync } from "node:fs";
import { createServer } from "node:http";
import { extname, join, resolve, sep } from "node:path";
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

/**
 * Every file in the build, found once at start-up, by the address it is served at ("/assets/index-abc.js") and the path on disk. A request is
 * answered from this list and from nothing else: what a visitor sends is only ever looked up in it, never put into a path, so no address
 * (with "..", an encoded slash, a null byte or a backslash) can reach a file that is not part of the build.
 */
const served = new Map();
(function walk(directory, prefix) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      walk(join(directory, entry.name), `${prefix}${entry.name}/`);
    } else if (entry.isFile()) {
      served.set(`${prefix}${entry.name}`, join(directory, entry.name));
    }
  }
})(dist, "/");

const index = served.get("/index.html");
if (index === undefined) {
  console.error(`${dist} has no index.html. Build the site first.`);
  process.exit(1);
}

/** The file for an address: the one with that exact name, or the page every other address is answered with. Null for an address that is not valid text. */
function locate(urlPath) {
  let decoded;
  try {
    decoded = decodeURIComponent(urlPath.split(/[?#]/)[0] ?? "/");
  } catch {
    return null; // a "%" that is not an escape
  }

  return served.get(decoded) ?? index;
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

  const file = locate(request.url ?? "/");
  if (file === null) {
    response.writeHead(400, { ...base, "Content-Type": "text/plain; charset=utf-8" });
    response.end("Bad request");

    return;
  }

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
}).listen(port, function listening() {
  // The port actually bound (it may have been asked for as 0, which means any free one).
  console.log(`Serving ${dist} on http://localhost:${String(this.address().port)}`);
});

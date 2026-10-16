import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";

const folders: string[] = [];

afterEach(() => {
  for (const folder of folders.splice(0)) rmSync(folder, { recursive: true, force: true });
});

const goodFiles: Record<string, string> = {
  "robots.txt": "User-agent: *\nDisallow: /\n",
  "_headers": "/*\n  Content-Security-Policy: default-src 'none'; frame-ancestors 'none'\n  Strict-Transport-Security: max-age=31536000; includeSubDomains\n  X-Robots-Tag: noindex, nofollow\n",
  "_redirects": "/* /index.html 200\n",
  "_routes.json": '{"version":1,"include":["/api/*"],"exclude":[]}',
};

/** Runs the build check, the one "npm run check:build" runs, on a made-up build with this index.html (and the other files, unless changed). */
function check(indexHtml: string, files: Record<string, string | null> = {}, script = "export const a = 1;"): { passed: boolean; output: string } {
  const folder = mkdtempSync(join(tmpdir(), "check-build-"));
  folders.push(folder);
  mkdirSync(join(folder, "assets"));
  writeFileSync(join(folder, "index.html"), indexHtml);
  writeFileSync(join(folder, "assets", "app.js"), script);
  for (const [name, content] of Object.entries({ ...goodFiles, ...files })) {
    if (content !== null) {
      writeFileSync(join(folder, name), content);
    }
  }

  const result = spawnSync(process.execPath, ["scripts/check-build.mjs", folder], { encoding: "utf8" });

  return { passed: result.status === 0, output: result.stdout + result.stderr };
}

const page = (script: string) => `<!doctype html><html><head><meta name="robots" content="noindex, nofollow"><title>x</title>${script}</head><body></body></html>`;

describe("the build check", () => {
  it.each([
    ["robots.txt is missing", { "robots.txt": null }],
    ["robots.txt allows crawling", { "robots.txt": "User-agent: *\nAllow: /\n" }],
    ["_headers is missing", { _headers: null }],
    ["_headers has no HSTS", { _headers: "/*\n  Content-Security-Policy: default-src 'none'; frame-ancestors 'none'\n  X-Robots-Tag: noindex\n" }],
    ["_headers has no policy that forbids framing", { _headers: "/*\n  Strict-Transport-Security: max-age=31536000\n  X-Robots-Tag: noindex\n" }],
    ["_headers has no noindex header", { _headers: "/*\n  Content-Security-Policy: default-src 'none'; frame-ancestors 'none'\n  Strict-Transport-Security: max-age=31536000\n" }],
    ["_redirects is missing", { _redirects: null }],
    ["_routes.json does not route /api/*", { "_routes.json": '{"version":1,"include":["/*"],"exclude":[]}' }],
  ])("fails when %s", (_name, files) => {
    expect(check(page(`<script type="module" src="/assets/app.js"></script>`), files).passed).toBe(false);
  });

  it("fails a page with no noindex meta tag", () => {
    const withoutMeta = `<!doctype html><html><head><title>x</title><script type="module" src="/assets/app.js"></script></head></html>`;

    expect(check(withoutMeta).passed).toBe(false);
  });

  it.each(["const h = 'X-Proxy-Secret';", "const s = process.env.PROXY_SECRET;"])("fails when the browser files name the proxy secret: %s", (code) => {
    const { passed, output } = check(page(`<script type="module" src="/assets/app.js"></script>`), {}, code);

    expect(passed).toBe(false);
    expect(output).toContain("proxy secret");
  });

  it("passes a page whose only script is a file", () => {
    expect(check(page(`<script type="module" src="/assets/app.js"></script>`)).passed).toBe(true);
  });

  it.each([
    ["a plain end tag", `<script>alert(1)</script>`],
    ["an end tag with a space", `<script>alert(1)</script >`],
    ["an end tag with an attribute", `<script>alert(1)</script foo="bar">`],
    ["an end tag in capitals, with a line break", `<SCRIPT>alert(1)</SCRIPT\n>`],
    ["an end tag with a slash", `<script>alert(1)</script/>`],
    ["a file script that also has code in it", `<script src="/assets/app.js">alert(1)</script >`],
    ["a script that never ends", `<script>alert(1)`],
  ])("fails an inline script with %s", (_name, script) => {
    const { passed, output } = check(page(script));

    expect(passed).toBe(false);
    expect(output).toMatch(/inline script|could not read/);
  });

  it("fails an inline style and an event handler", () => {
    expect(check(page(`<style>a{}</style>`)).passed).toBe(false);
    expect(check(`<body onload="x()"></body>`).passed).toBe(false);
  });
});

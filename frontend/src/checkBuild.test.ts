import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";

const folders: string[] = [];

afterEach(() => {
  for (const folder of folders.splice(0)) rmSync(folder, { recursive: true, force: true });
});

/** Runs the build check, the one "npm run check:build" runs, on a made-up build with this index.html. */
function check(indexHtml: string): { passed: boolean; output: string } {
  const folder = mkdtempSync(join(tmpdir(), "check-build-"));
  folders.push(folder);
  mkdirSync(join(folder, "assets"));
  writeFileSync(join(folder, "index.html"), indexHtml);
  writeFileSync(join(folder, "assets", "app.js"), "export const a = 1;");
  const result = spawnSync(process.execPath, ["scripts/check-build.mjs", folder], { encoding: "utf8" });

  return { passed: result.status === 0, output: result.stdout + result.stderr };
}

const page = (script: string) => `<!doctype html><html><head><title>x</title>${script}</head><body></body></html>`;

describe("the build check", () => {
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

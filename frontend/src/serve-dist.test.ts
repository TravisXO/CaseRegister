import { spawn, type ChildProcess } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { request } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

// The server the browser checks use (scripts/serve-dist.mjs) answers from a list of the files in the build and from nothing else, so no address
// a visitor sends can reach a file outside it. Code scanning flagged the earlier version, which put the address into a path; these are the
// addresses that would have shown it going wrong. They are sent raw, because a browser or fetch tidies ".." away before sending.

const root = mkdtempSync(join(tmpdir(), "serve-dist-"));
const site = join(root, "site");
const secret = "SECRET-OUTSIDE-THE-BUILD";
let server: ChildProcess;
let port = 0;

beforeAll(async () => {
  mkdirSync(join(site, "assets"), { recursive: true });
  writeFileSync(join(site, "index.html"), "<!doctype html><title>the page</title>");
  writeFileSync(join(site, "assets", "app.js"), "console.log('app');");
  writeFileSync(join(root, "secret.txt"), secret); // next to the build, not in it
  writeFileSync(join(site, "..secret"), "a file whose name starts with two dots, which is part of the build and may be served");

  server = spawn(process.execPath, ["scripts/serve-dist.mjs", site, "0"], { stdio: ["ignore", "pipe", "inherit"] });
  port = await new Promise<number>((resolve, reject) => {
    server.stdout?.on("data", (chunk: Buffer) => {
      const match = /localhost:(\d+)/.exec(chunk.toString());
      if (match?.[1] !== undefined) {
        resolve(Number(match[1]));
      }
    });
    server.once("exit", (code) => {
      reject(new Error(`the server stopped with ${String(code)}`));
    });
  });
});

afterAll(() => {
  server.kill();
  rmSync(root, { recursive: true, force: true });
});

function get(path: string, headers: Record<string, string> = {}): Promise<{ status: number; body: string; headers: Record<string, string | string[] | undefined> }> {
  return new Promise((resolve, reject) => {
    const outgoing = request({ host: "127.0.0.1", port, path, method: "GET", headers }, (response) => {
      const chunks: Buffer[] = [];
      response.on("data", (chunk: Buffer) => chunks.push(chunk));
      response.on("end", () => {
        resolve({ status: response.statusCode ?? 0, body: Buffer.concat(chunks).toString(), headers: response.headers });
      });
    });
    outgoing.on("error", reject);
    outgoing.end();
  });
}

describe("the server for the browser checks", () => {
  it("serves a file of the build, and the page for any other address", async () => {
    expect((await get("/assets/app.js")).body).toBe("console.log('app');");
    expect((await get("/profile")).body).toContain("the page");
    expect((await get("/")).body).toContain("the page");
    expect((await get("/?x=1#y")).body).toContain("the page");
  });

  it.each([
    "/../secret.txt",
    "/../../secret.txt",
    "/%2e%2e/secret.txt",
    "/%2E%2E%2Fsecret.txt",
    "/assets/../../secret.txt",
    "/assets/%2e%2e/%2e%2e/secret.txt",
    "/..%2fsecret.txt",
    "/..%5csecret.txt",
    "/%5c..%5csecret.txt",
    String.raw`/\..\secret.txt`,
    "//secret.txt",
    "/%00/../secret.txt",
    "/assets/app.js%00.txt",
    "/secret.txt",
  ])("never serves a file outside the build for %s", async (path) => {
    const answer = await get(path);

    expect(answer.body).not.toContain(secret);
    expect([200, 400]).toContain(answer.status); // the page, or a refusal
  });

  it("answers an address that is not valid text with 400 and keeps running", async () => {
    expect((await get("/%")).status).toBe(400);
    expect((await get("/%E0%A4%A")).status).toBe(400);
    expect((await get("/assets/app.js")).status).toBe(200); // still up
  });

  it("serves a file whose name starts with two dots when it is in the build, and only as itself", async () => {
    expect((await get("/..secret")).body).toContain("starts with two dots");
  });

  it("sends the production headers on every answer, including a refusal", async () => {
    for (const path of ["/profile", "/assets/app.js", "/%"]) {
      const answer = await get(path);
      expect(answer.headers["x-content-type-options"]).toBe("nosniff");
      expect(String(answer.headers["content-security-policy"])).toContain("object-src 'none'");
    }
  });

  it("compresses what can be compressed, for a client that accepts it", async () => {
    expect((await get("/assets/app.js", { "Accept-Encoding": "gzip" })).headers["content-encoding"]).toBe("gzip");
    expect((await get("/assets/app.js")).headers["content-encoding"]).toBeUndefined();
  });
});

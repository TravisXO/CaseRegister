import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { ESLint } from "eslint";
import react from "eslint-plugin-react";
import { http, HttpResponse } from "msw";
import { describe, expect, it, vi } from "vitest";
import tseslint from "typescript-eslint";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { securityRules } from "../eslint.security.js";
import { agent, fakeApi, problem, renderApp, signedInAs } from "./test/helpers";
import { server } from "./test/server";

/** Lints a snippet with the very rules the project uses, and returns what they said. */
async function lint(code: string): Promise<string[]> {
  const eslint = new ESLint({
    overrideConfigFile: true,
    overrideConfig: [
      {
        files: ["**/*.tsx"],
        languageOptions: { parser: tseslint.parser, parserOptions: { ecmaFeatures: { jsx: true } } },
        plugins: { react },
        rules: securityRules,
      },
    ],
  });
  const [result] = await eslint.lintText(code, { filePath: "probe.tsx" });

  return (result?.messages ?? []).map((message) => `${message.ruleId ?? "parse"}: ${message.message}`);
}

describe("the lint rules that keep the browser side safe", () => {
  it.each([
    ["localStorage.setItem('a', 'b');", /localStorage or sessionStorage/],
    ["const x = localStorage.getItem('a');", /localStorage or sessionStorage/],
    ["sessionStorage.setItem('a', 'b');", /localStorage or sessionStorage/],
    ["window.localStorage.setItem('a', 'b');", /localStorage/],
    ["window.sessionStorage.clear();", /sessionStorage/],
    ["globalThis.localStorage.clear();", /localStorage/],
    ["globalThis.sessionStorage.clear();", /sessionStorage/],
    ["window['localStorage'].setItem('a', 'b');", /localStorage or sessionStorage/],
    ["const store = window; store.sessionStorage.clear();", /localStorage or sessionStorage/],
    ["const { localStorage } = window; localStorage.clear();", /localStorage/],
    ["indexedDB.open('x');", /browser's databases/],
    ["document.cookie = 'a=b';", /Cookies/],
    ["setInterval(() => undefined, 1000);", /repeating timers/i],
    ["window.setInterval(() => undefined, 1000);", /repeating timers/i],
    ["requestAnimationFrame(() => undefined);", /background loops/i],
    ["eval('1 + 1');", /eval/i],
    ["new Function('return 1');", /dynamic code/i],
    ["new EventSource('/api/x');", /background connections/i],
    ["new WebSocket('wss://x');", /background connections/i],
    ["const x = <div dangerouslySetInnerHTML={{ __html: 'x' }} />;", /dangerouslySetInnerHTML/],
    ["const x = <a href='https://example.test' target='_blank'>x</a>;", /noreferrer/],
    ["const x = <a href={link} target='_blank' rel='noopener'>x</a>;", /noreferrer/],
    ["const x = <a href='javascript:alert(1)'>x</a>;", /javascript:/i],
  ])("refuses %s", async (code, expected) => {
    const messages = await lint(code);

    expect(messages.join("\n")).toMatch(expected);
  });

  it.each([
    ["const x = <a href='https://example.test' target='_blank' rel='noopener noreferrer'>x</a>;"],
    ["const x = <p>{String(1)}</p>;"],
    ["setTimeout(() => undefined, 10);"],
    ["const local = 'Storage'; void local;"],
  ])("lets %s through", async (code) => {
    expect(await lint(code)).toEqual([]);
  });

  it("is in force over the application's own code", async () => {
    const project = new ESLint();
    const results = await project.lintFiles(["src/auth/session.tsx", "src/components/Layout.tsx", "src/lib/hooks.ts"]);

    expect(results.flatMap((result) => result.messages)).toEqual([]);
  }, 120_000); // linting with type information is slow the first time
});

describe("what the site is served under", () => {
  const headers = readFileSync("public/_headers", "utf8");
  const policy = /Content-Security-Policy:\s*(.+)/.exec(headers)?.[1] ?? "";
  const directives = Object.fromEntries(policy.split(";").map((part) => part.trim().split(/\s+/)).filter((parts) => parts[0] !== "").map(([name, ...values]) => [name, values]));

  it("has a content security policy at all", () => {
    expect(policy).not.toBe("");
  });

  it("allows no inline script and no eval", () => {
    expect(policy).not.toContain("'unsafe-inline'");
    expect(policy).not.toContain("'unsafe-eval'");
    expect(policy).not.toContain("'unsafe-hashes'");
    expect(policy).not.toContain("data:text/javascript");
    expect(directives["script-src"]).toEqual(["'self'", "https://challenges.cloudflare.com"]);
  });

  it("refuses everything not named, and cannot be framed or have its base changed", () => {
    expect(directives["default-src"]).toEqual(["'none'"]);
    expect(directives["frame-ancestors"]).toEqual(["'none'"]);
    expect(directives["base-uri"]).toEqual(["'none'"]);
    expect(directives["form-action"]).toEqual(["'self'"]);
  });

  it("names no outside origin except Cloudflare Turnstile's, and no wildcard", () => {
    const origins = [...policy.matchAll(/https?:\/\/[^\s;]+/g)].map((match) => match[0]);

    expect(new Set(origins)).toEqual(new Set(["https://challenges.cloudflare.com"]));
    expect(policy).not.toMatch(/(^|\s)\*(\s|;|$)/);
    expect(directives["connect-src"]).toEqual(["'self'", "https://challenges.cloudflare.com"]);
    expect(directives["frame-src"]).toEqual(["https://challenges.cloudflare.com"]);
    expect(directives["style-src"]).toEqual(["'self'"]);
  });

  it("has the other security headers too", () => {
    expect(headers).toMatch(/X-Content-Type-Options: nosniff/);
    expect(headers).toMatch(/Referrer-Policy: no-referrer/);
    expect(headers).toMatch(/Permissions-Policy: .*camera=\(\)/);
  });

  it("sends every path to the app, so addresses like /profile work on a reload", () => {
    expect(readFileSync("public/_redirects", "utf8")).toMatch(/^\/\*\s+\/index\.html\s+200/);
  });

  it("tells browsers to keep to https for a year, and cannot be framed by anything", () => {
    expect(headers).toMatch(/Strict-Transport-Security: max-age=31536000; includeSubDomains\s*$/m);
    expect(headers).not.toMatch(/Strict-Transport-Security:.*preload/);
    expect(directives["frame-ancestors"]).toEqual(["'none'"]);
  });

  it("keeps the site out of search engines three ways: a header, a meta tag and robots.txt", () => {
    expect(headers).toMatch(/X-Robots-Tag: noindex, nofollow/);
    expect(readFileSync("index.html", "utf8")).toMatch(/<meta name="robots" content="[^"]*noindex[^"]*"/);
    expect(readFileSync("public/robots.txt", "utf8")).toMatch(/^User-agent: \*\s*\nDisallow: \/\s*$/m);
  });

  it("keeps built files for a year and never lets the page itself or its other addresses be cached for long", () => {
    const [everything = "", assets = ""] = headers.split(/^\/assets\/\*$/m);

    expect(everything).toMatch(/Cache-Control: no-cache\s*$/m);
    expect(assets).toMatch(/^\s+! Cache-Control$/m);
    expect(assets).toMatch(/Cache-Control: public, max-age=31536000, immutable/);
    expect(everything).not.toContain("immutable");
  });

  it("names no secret, key or token anywhere in the headers or the redirects", () => {
    for (const text of [headers, readFileSync("public/_redirects", "utf8"), readFileSync("public/_routes.json", "utf8")]) {
      expect(text).not.toMatch(/secret|password|api[_-]?key|token\s*[:=]\s*\S{12,}/i);
    }
  });

  it("sends only /api/* to the Pages Function, which sits where Pages looks for it", () => {
    const routes = JSON.parse(readFileSync("public/_routes.json", "utf8")) as { version: number; include: string[]; exclude: string[] };

    expect(routes).toEqual({ version: 1, include: ["/api/*"], exclude: [] });
    expect(existsSync("functions/api/[[path]].ts")).toBe(true);
  });

  it("serves the preview under the same first block of headers, without the one a browser would remember for localhost", () => {
    const config = readFileSync("vite.config.ts", "utf8");

    expect(config).toContain('match[1] !== "Strict-Transport-Security"');
    expect(config).toContain("blocks === 1");
  });
});

const allowedHosts = new Set(["challenges.cloudflare.com", "localhost"]);

/** The web addresses in some text whose host is not exactly one of the allowed ones. Compares the host, never a piece of the text. */
function outsideAddresses(text: string): string[] {
  return [...text.matchAll(/https?:\/\/[^\s"'`)]+/g)].map((match) => match[0]).filter((address) => {
    try {
      return !allowedHosts.has(new URL(address).hostname);
    } catch {
      return true; // an address that cannot be read is not waved through
    }
  });
}

describe("the address check", () => {
  it("allows the exact hosts, with any path or port", () => {
    expect(outsideAddresses("https://challenges.cloudflare.com/turnstile/v0/api.js and https://localhost:5173/staff/setup")).toEqual([]);
  });

  it.each([
    "https://challenges.cloudflare.com.evil.example/api.js",
    "https://evil.example/?next=challenges.cloudflare.com",
    "https://evil.example/challenges.cloudflare.com",
    "https://challenges.cloudflare.com@evil.example/",
    "https://localhost.evil.example/",
    "https://notchallenges.cloudflare.com/",
  ])("does not allow %s", (address) => {
    expect(outsideAddresses(`see ${address} here`)).toEqual([address]);
  });
});

function sourceFiles(directory: string): string[] {
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name);
    if (statSync(path).isDirectory()) {
      return name === "test" ? [] : sourceFiles(path);
    }

    return /\.(ts|tsx)$/.test(name) && !/\.test\.|schema\.d\.ts/.test(name) ? [path] : [];
  });
}

describe("the source", () => {
  const files = sourceFiles("src");

  it("names no outside address except Turnstile's script", () => {
    // Allowed: Turnstile's script address, and localhost in a comment's example of the staff setup link.
    const found = files.flatMap((file) => outsideAddresses(readFileSync(file, "utf8")).map((address) => `${relative("src", file)}: ${address}`));

    expect(found).toEqual([]);
  });

  it("never touches the browser's storage while the app is used", async () => {
    const setItem = vi.spyOn(Storage.prototype, "setItem");
    const getItem = vi.spyOn(Storage.prototype, "getItem");
    const removeItem = vi.spyOn(Storage.prototype, "removeItem");
    server.use(http.get("/api/auth/session", () => problem(401, "Unauthorized")));
    fakeApi({ "POST /api/auth/sign-in": () => ({ status: "two-factor-required" }) });
    renderApp("/sign-in");
    await userEvent.type(await screen.findByLabelText("Email address"), "ada@example.test");
    await userEvent.type(screen.getByLabelText("Password"), "correct horse battery staple");

    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
    await screen.findByRole("heading", { name: "Second step" });

    // Nothing is written, and nothing of ours is read. (The router library itself looks for two feature flags, and finds none.)
    expect(setItem).not.toHaveBeenCalled();
    expect(removeItem).not.toHaveBeenCalled();
    expect(getItem.mock.calls.map((call) => call[0]).filter((key) => key !== "remix-router-transitions" && key !== "debugLevel")).toEqual([]);
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });
});

describe("background calls", () => {
  it("makes none: hours can pass on a page and the API is not asked anything", async () => {
    signedInAs(agent);
    server.use(http.get("/api/me/practising-certificates", () => HttpResponse.json([])));
    const seen: string[] = [];
    server.events.on("request:start", ({ request }) => {
      seen.push(new URL(request.url).pathname);
    });
    renderApp("/profile");
    await screen.findByText("Ada Mwansa");
    await screen.findByText("You have not added a certificate yet.");
    await waitFor(() => { expect(seen).toContain("/api/notifications"); }); // the bell asks once, when the page opens
    const before = seen.length;

    vi.useFakeTimers();
    try {
      await vi.advanceTimersByTimeAsync(3 * 60 * 60 * 1000);
    } finally {
      vi.useRealTimers();
    }

    expect(seen.length).toBe(before);
    expect(seen.sort()).toEqual(["/api/auth/session", "/api/me", "/api/me/practising-certificates", "/api/notifications"]);
    server.events.removeAllListeners();
  });
});

import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import type { ServerOptions } from "vite";
import { defineConfig } from "vitest/config";

// The local API (the "https" launch profile). The browser only ever talks to the Vite server, so it sees one site,
// as it will through the Cloudflare proxy in production.
const apiTarget = process.env.API_URL ?? "https://localhost:7205";

const certificate = resolve(".certs/dev.pem");
const key = resolve(".certs/dev.key");

/** The ASP.NET Core development certificate, exported by "npm run cert", so the browser trusts this site as it trusts the API. */
function localHttps(): ServerOptions["https"] {

  if (!existsSync(certificate) || !existsSync(key)) {
    throw new Error("No development certificate yet. Run: npm run cert");
  }

  return { cert: readFileSync(certificate), key: readFileSync(key) };
}

/**
 * The headers every page gets in public/_headers (its first block, "/*"), so "npm run preview" serves the production build under the
 * production policy. Not Strict-Transport-Security: a browser that saw it from localhost could refuse plain http on localhost afterwards.
 */
function productionHeaders(): Record<string, string> {
  const headers: Record<string, string> = {};
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

const proxy = {
  "/api": { target: apiTarget, secure: false, xfwd: true },
};

export default defineConfig(({ command }) => {
  // Only running the site (npm run dev, npm run preview) needs the certificate. Building it, and the tests, do not.
  const running = command === "serve" && !process.env.VITEST;

  return {
  plugins: [react(), tailwindcss()],
  server: { https: running ? localHttps() : undefined, port: 5173, strictPort: true, proxy },
  preview: { https: running ? localHttps() : undefined, port: 4173, strictPort: true, proxy, headers: running ? productionHeaders() : {} },
  build: {
    sourcemap: false, // nothing in production points back to the source
    target: "es2022",
    assetsInlineLimit: 0, // no data: URLs inside the bundle
    modulePreload: { polyfill: false }, // no extra inline-style code paths
  },
  test: {
    environment: "jsdom",
    // The browser checks in e2e/ are run by Playwright (npm run e2e), not here.
    include: ["src/**/*.test.{ts,tsx}", "functions/**/*.test.ts"],
    setupFiles: ["./src/test/setup.ts"],
    css: false,
    restoreMocks: true,
    testTimeout: 30000,
  },
  };
});

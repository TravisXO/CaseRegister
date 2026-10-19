import { defineConfig, devices } from "@playwright/test";

// The built site, served under its production headers (scripts/serve-dist.mjs), with every call to the API answered by the tests themselves.
// Build it first with a Turnstile site key (any value; the test replaces Cloudflare's script): npm run build:e2e
const port = 4173;

export default defineConfig({
  testDir: "e2e",
  fullyParallel: true,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  reporter: process.env.CI ? [["list"], ["html", { open: "never" }]] : "list",
  use: {
    baseURL: `http://localhost:${String(port)}`,
    trace: "retain-on-failure",
    // A browser already on the machine can stand in for the one Playwright downloads: PLAYWRIGHT_CHANNEL=msedge (or chrome).
    ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}),
  },
  projects: [
    { name: "desktop", use: { ...devices["Desktop Chrome"], viewport: { width: 1280, height: 800 } } },
    // 320 CSS pixels wide is what 400% zoom on a 1280px screen gives, and the narrowest width WCAG 2.2 asks the layout to work at.
    { name: "narrow-320", use: { ...devices["Desktop Chrome"], viewport: { width: 320, height: 640 } } },
  ],
  webServer: { command: `node scripts/serve-dist.mjs dist-e2e ${String(port)}`, url: `http://localhost:${String(port)}`, reuseExistingServer: !process.env.CI, timeout: 30_000 },
});

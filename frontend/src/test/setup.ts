import "@testing-library/jest-dom/vitest";
import { cleanup, configure } from "@testing-library/react";
import { afterAll, afterEach, beforeAll, beforeEach, vi } from "vitest";
import { forgetCsrfToken, onSessionEnded } from "../api/client";
import { expectNoAccessibilityViolations, screenRendered } from "./a11y";
import { server, unhandledRequests } from "./server";

vi.stubEnv("VITE_TURNSTILE_SITE_KEY", "test-site-key");

// A slow or busy machine (a continuous-integration runner, say) should not make a test fail for waiting a second.
configure({ asyncUtilTimeout: 5000 });

// The tests run in Node, whose fetch needs a full address. The app only ever uses addresses on its own site (/api/...), as in a browser.
const nodeFetch = globalThis.fetch;
globalThis.fetch = (input: RequestInfo | URL, init?: RequestInit) =>
  nodeFetch(typeof input === "string" && input.startsWith("/") ? new URL(input, window.location.href) : input, init);

beforeAll(() => {
  // "error" refuses the request; the callback also writes it down so the test that made it fails (see afterEach), even if the app hid the failure.
  server.listen({
    onUnhandledFrame: async ({ frame, defaults }) => {
      unhandledRequests.push(await frame.getUnhandledMessage());
      defaults.error();
    },
  });
});

beforeEach(() => {
  // A stand-in for Cloudflare's script: the check passes at once, and gives a new token each time it is reset.
  let count = 0;
  window.turnstile = {
    render: (_container, options) => {
      count += 1;
      (options.callback as (token: string) => void)(`token-${count}`);

      return "widget-1";
    },
    reset: () => undefined,
    remove: () => undefined,
  };
});

afterEach(async () => {
  try {
    // Every screen a test has rendered is checked, as the test left it, for what automated tools can find (axe: WCAG 2.2 AA and best practices).
    if (screenRendered.value) {
      screenRendered.value = false;
      await expectNoAccessibilityViolations();
    }

    const missed = unhandledRequests.splice(0);
    if (missed.length > 0) {
      throw new Error(`The app made requests the test did not answer. Add a handler for each (in the test, or in test/server.ts if every test needs it): ${missed.join("; ")}`);
    }
  } finally {
    // Whatever was found, the next test starts clean.
    cleanup();
    server.resetHandlers();
    forgetCsrfToken();
    onSessionEnded(null);
    window.history.replaceState(null, "", "/");
  }
});

afterAll(() => {
  server.close();
});

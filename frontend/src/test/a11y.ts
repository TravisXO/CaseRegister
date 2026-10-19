import axe from "axe-core";
import { expect } from "vitest";
import * as matchers from "vitest-axe/matchers";
import type { AxeMatchers } from "vitest-axe/matchers";

expect.extend(matchers);

/** Set when a test has rendered a whole screen with renderApp; the shared setup then checks it for accessibility problems when the test ends. */
export const screenRendered = { value: false };

// Colour contrast needs a real browser (jsdom has no layout or colours): the Playwright job checks it on the built site.
const rules = { "color-contrast": { enabled: false } };

/** Fails with each problem axe finds, named and explained, in the WCAG 2.2 AA rules and the best practices. */
export async function expectNoAccessibilityViolations(container: Element = document.body): Promise<void> {
  const results = await axe.run(container, { rules, runOnly: { type: "tag", values: ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa", "best-practice"] } });
  // vitest-axe declares its matcher for an older vitest, so it is reached through its own type.
  (expect(results) as unknown as AxeMatchers).toHaveNoViolations();
}

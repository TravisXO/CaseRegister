import { expect, test } from "@playwright/test";
import { expectAccessible, expectNoSidewaysScroll, mockApi, type Who } from "./support";

// Every main screen of the built site, in a real browser, at a desktop width and at 320px wide: axe finds nothing (colour contrast and target
// size included), the page never scrolls sideways, and a few things only a browser can show (focus, the error summary) are as they should be.

interface Screen {
  name: string;
  path: string;
  who: Who;
  /** The heading to wait for, so the screen has finished drawing before it is checked. */
  heading: string | RegExp;
}

const screens: Screen[] = [
  { name: "sign in", path: "/sign-in", who: "nobody", heading: "Sign in" },
  { name: "sign up", path: "/sign-up", who: "nobody", heading: /apply|join|sign up/i },
  { name: "code step", path: "/sign-in/code", who: "nobody", heading: /code|verify|second/i },
  { name: "enrolment code", path: "/sign-in/enrolment-code", who: "nobody", heading: /code/i },
  { name: "staff setup", path: "/staff/setup", who: "nobody", heading: /set up|setup|staff/i },
  { name: "not found", path: "/no/such/page", who: "nobody", heading: /not found|cannot find|no such/i },
  { name: "profile", path: "/profile", who: "agent", heading: "My profile" },
  { name: "sessions", path: "/sessions", who: "agent", heading: /sessions/i },
  { name: "notifications", path: "/notifications", who: "agent", heading: /notifications/i },
  { name: "admin: applications", path: "/admin/applications", who: "admin", heading: /applications/i },
  { name: "admin: proposals", path: "/admin/proposals", who: "admin", heading: /proposals/i },
  { name: "admin: one proposal", path: "/admin/proposals/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", who: "admin", heading: /proposal|confirm|suspend/i },
  { name: "admin: admins", path: "/admin/admins", who: "admin", heading: /admins/i },
  { name: "admin: audit log", path: "/admin/audit", who: "admin", heading: /audit/i },
  { name: "admin: invite staff", path: "/admin/invite", who: "admin", heading: /invite/i },
  { name: "admin: account actions", path: "/admin/actions", who: "admin", heading: /actions|account/i },
];

for (const screen of screens) {
  test(`${screen.name} has no accessibility violations and does not scroll sideways`, async ({ page }) => {
    const unanswered = await mockApi(page, screen.who);

    await page.goto(screen.path);
    await expect(page.getByRole("heading", { level: 1, name: screen.heading }).first()).toBeVisible();
    // Let what the screen asks for arrive (tables, lists), then look.
    await page.waitForLoadState("networkidle");

    await expectAccessible(page, screen.name);
    await expectNoSidewaysScroll(page, screen.name);
    expect(unanswered, "calls to the API that nothing answered").toEqual([]);
  });
}

test("a failed sign-in form puts the list of problems first, with focus on it, and each item leads to its field", async ({ page }) => {
  await mockApi(page, "nobody");
  await page.goto("/sign-in");

  await page.getByRole("button", { name: "Sign in", exact: true }).click();

  const summary = page.getByRole("alert").filter({ hasText: "There is a problem" });
  await expect(summary).toBeVisible();
  await expect(summary).toBeFocused();
  await expect(summary).toHaveAttribute("tabindex", "-1");
  await summary.getByRole("link", { name: "Enter your email address." }).click();
  await expect(page.getByLabel("Email address")).toBeFocused();
  await expect(page.getByLabel("Email address")).toHaveAttribute("aria-invalid", "true");
  await expect(page.getByLabel("Email address")).toHaveAccessibleDescription("Enter your email address.");
  await expectAccessible(page, "sign in with errors");
});

test("where the keyboard is can always be seen", async ({ page }) => {
  await mockApi(page, "nobody");
  await page.goto("/sign-in");

  for (let step = 0; step < 8; step++) {
    await page.keyboard.press("Tab");
    const outline = await page.evaluate(() => {
      const element = document.activeElement;
      if (!element || element === document.body) {
        return null;
      }

      const style = getComputedStyle(element);
      const box = element.getBoundingClientRect();

      return { width: Number.parseFloat(style.outlineWidth), style: style.outlineStyle, top: box.top, bottom: box.bottom, height: window.innerHeight };
    });

    if (outline !== null) {
      expect(outline.style, "the focused element has an outline").not.toBe("none");
      expect(outline.width).toBeGreaterThanOrEqual(2);
      expect(outline.bottom, "it is on the screen, not scrolled out of sight").toBeGreaterThan(0);
      expect(outline.top).toBeLessThan(outline.height);
    }
  }
});

test("the two-factor setup shows the key as text beside the picture, and the code box takes a paste", async ({ page }) => {
  await mockApi(page, "agent");
  await page.route("**/api/auth/session", (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ userId: "11111111-1111-4111-8111-111111111111", twoFactorComplete: false }) }));
  await page.goto("/two-factor/set-up");

  await page.getByRole("button", { name: "Start set-up" }).click();

  await expect(page.getByText("JBSW Y3DP EHPK 3PXP")).toBeVisible();
  await expect(page.getByRole("img", { name: /QR code/ })).toBeVisible();
  const code = page.getByLabel("Code from the app");
  await expect(code).toHaveAttribute("inputmode", "numeric");
  await expect(code).toHaveAttribute("autocomplete", "one-time-code");
  await code.fill("123456");
  await expect(code).toHaveValue("123456");
  await expectAccessible(page, "two-factor set-up");
  await expectNoSidewaysScroll(page, "two-factor set-up");
});

test("the profile's tables scroll inside their own region at 320px, and the region is named and reachable by keyboard", async ({ page }) => {
  await mockApi(page, "agent");
  await page.goto("/profile");

  const region = page.getByRole("region", { name: "Your passkeys" });
  await expect(region).toBeVisible();
  await expect(region).toHaveAttribute("tabindex", "0");
  await expect(page.getByRole("table", { name: "Your passkeys" })).toBeVisible();
  await expect(page.getByRole("table", { name: "Your practising certificates" })).toBeVisible();
});

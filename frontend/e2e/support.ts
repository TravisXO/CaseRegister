import AxeBuilder from "@axe-core/playwright";
import { expect, type Page, type Route } from "@playwright/test";

// The API, as far as the screens need it, answered by the test: the browser loads the real built site and nothing else. A call nobody
// answered fails the test, so a screen cannot quietly depend on something these checks never saw.

export type Who = "nobody" | "agent" | "admin";

const agent = {
  id: "11111111-1111-4111-8111-111111111111",
  fullName: "Ada Mwansa",
  email: "ada@example.test",
  membershipNumber: "AGT-0101",
  membershipClass: "Full",
  agency: { id: "22222222-2222-4222-8222-222222222222", name: "Kafue Properties", isActive: true },
  roles: ["RegisteredAgent"],
  hasCurrentPractisingCertificate: true,
  lastSignIn: { at: "2026-10-10T08:00:00+00:00", country: "ZM", city: "Lusaka" },
  recoveryCodesRemaining: 10,
  recoveryCodesLow: false,
};

const admin = { ...agent, id: "33333333-3333-4333-8333-333333333333", fullName: "Brian Phiri", email: "brian@example.test", membershipNumber: null, membershipClass: null, agency: null, roles: ["Admin"], hasCurrentPractisingCertificate: false };

const proposal = {
  id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
  type: "SuspendUser",
  target: { userId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", agencyId: null, email: null, name: "Chanda Banda" },
  description: "Suspend the account of Chanda Banda",
  payload: {},
  payloadHash: "9f2c4e7a1b6d8035c4e1a7f2b9d06e3c58a1f4b7d2e9c06a3f5b8d1e4c7a2f90",
  reason: "Left the association; confirmed by phone.",
  status: "Pending",
  proposedBy: { userId: "cccccccc-cccc-4ccc-8ccc-cccccccccccc", name: "Carol Zulu" },
  proposedAt: "2026-10-13T08:05:00+00:00",
  expiresAt: "2026-10-16T08:05:00+00:00",
  decidedByUserId: null,
  decidedAt: null,
  decisionReason: null,
  decidedByName: null,
  lastRefusal: null,
};

const audit = {
  entries: [
    { sequence: 12, occurredAt: "2026-10-13T08:05:00+00:00", actorType: "user", actorUserId: admin.id, actorRole: "Admin", action: "ActionProposed", targetType: "User", targetId: proposal.target.userId, agencyId: null, correlationId: null, ipAddress: "203.0.113.5", detail: "proposed: Suspend the account of Chanda Banda", before: null, after: { status: "Pending" }, actorName: "Brian Phiri", targetName: "Chanda Banda" },
    { sequence: 11, occurredAt: "2026-10-13T07:00:00+00:00", actorType: "user", actorUserId: agent.id, actorRole: "RegisteredAgent", action: "PasskeyAdded", targetType: "User", targetId: agent.id, agencyId: null, correlationId: null, ipAddress: null, detail: "passkey 1 of 10 (user verified)", before: null, after: null, actorName: "Ada Mwansa", targetName: "Ada Mwansa" },
  ],
  page: 1,
  pageSize: 25,
  total: 2,
};

/** Cloudflare's script, replaced by one that draws nothing and passes at once: the check itself is Cloudflare's, not ours. */
const turnstileStub = `window.turnstile = { render: (container, options) => { options.callback('test-token'); return 'widget'; }, reset: () => {}, remove: () => {} };`;

export async function mockApi(page: Page, who: Who): Promise<string[]> {
  const unanswered: string[] = [];
  const signedIn = who !== "nobody";
  const profile = who === "admin" ? admin : agent;
  const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: status >= 400 ? "application/problem+json" : "application/json", body: JSON.stringify(body) });

  await page.route("https://challenges.cloudflare.com/**", (route) => route.fulfill({ status: 200, contentType: "text/javascript", body: turnstileStub }));

  await page.route("**/api/**", (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const key = `${request.method()} ${path}`;

    switch (key) {
      case "GET /api/deployment":
        return json(route, { mode: "Demo" });
      case "GET /api/auth/csrf":
        return json(route, { token: "csrf-token" });
      case "GET /api/auth/session":
        return signedIn ? json(route, { userId: profile.id, twoFactorComplete: true, sessionEndsAt: new Date(Date.now() + 6 * 3_600_000).toISOString(), idleTimeoutSeconds: 1800 }) : json(route, { status: 401, title: "Unauthorized" }, 401);
      case "GET /api/me":
        return signedIn ? json(route, profile) : json(route, { status: 401, title: "Unauthorized" }, 401);
      case "GET /api/me/practising-certificates":
        return json(route, [{ id: "e5e5e5e5-e5e5-4e5e-8e5e-e5e5e5e5e5e5", number: "9611/2026", validFrom: "2026-01-01", validTo: "2026-12-31", isVerified: true, verifiedAt: "2026-10-01T08:00:00+00:00", isCurrent: true }]);
      case "GET /api/auth/passkeys":
        return json(route, [{ id: "cGFzc2tleS1sYXB0b3A", name: "Work laptop", createdAt: "2026-10-10T08:00:00+00:00", backedUp: true, transports: ["internal"] }]);
      case "GET /api/auth/sessions":
        return json(route, [{ id: "dddddddd-dddd-4ddd-8ddd-dddddddddddd", signedInAt: "2026-10-18T08:00:00+00:00", lastActiveAt: "2026-10-18T09:00:00+00:00", ipAddress: "203.0.113.5", userAgent: "Chrome on Windows", isCurrent: true }]);
      case "GET /api/notifications":
        return json(route, { unreadCount: 1, needsAcknowledgementCount: 0, items: [{ id: "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee", type: "PasskeyAdvice", severity: "Info", text: "Add a passkey to your account for a sign-in that cannot be phished.", link: "/profile", createdAt: "2026-10-18T08:00:00+00:00", readAt: null, acknowledgedAt: null, needsAcknowledgement: false }] });
      case "GET /api/admin/applications":
        return json(route, []);
      case "GET /api/admin/pending-actions":
        return json(route, [proposal]);
      case "GET /api/admin/admins":
        return json(route, { admins: [{ id: admin.id, fullName: "Brian Phiri", isActive: true, twoFactorOn: true }], activeCount: 1, minimum: 2, recommended: 3, warning: true });
      case "GET /api/audit/entries":
        return json(route, audit);
      case "POST /api/auth/2fa/enrol":
        return json(route, { otpAuthUri: "otpauth://totp/CaseRegister:ada%40example.test?secret=JBSWY3DPEHPK3PXP&issuer=CaseRegister", sharedKey: "JBSWY3DPEHPK3PXP" });
      default:
        if (request.method() === "GET" && path === `/api/admin/pending-actions/${proposal.id}`) {
          return json(route, proposal);
        }

        unanswered.push(key);

        return json(route, { status: 404, title: "Not mocked", detail: key }, 404);
    }
  });

  return unanswered;
}

/** No problem axe can find in a real browser: WCAG 2.0 to 2.2 level A and AA, and its best practices. This includes colour contrast and target size. */
export async function expectAccessible(page: Page, label: string): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa", "best-practice"]).analyze();
  const summary = results.violations.map((violation) => `${violation.id}: ${violation.help} (${violation.nodes.length}) e.g. ${violation.nodes[0]?.target.join(" ")}`);

  expect(summary, `${label}: accessibility violations`).toEqual([]);
}

/** The page does not scroll sideways: whatever is wider than the screen scrolls inside its own labelled region. */
export async function expectNoSidewaysScroll(page: Page, label: string): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

  expect(overflow, `${label}: the page scrolls sideways by ${String(overflow)}px`).toBeLessThanOrEqual(0);
}

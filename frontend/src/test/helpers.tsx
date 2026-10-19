import { render } from "@testing-library/react";
import { http, HttpResponse, type JsonBodyType } from "msw";
import { RouterProvider } from "react-router-dom";
import { createTestRouter } from "../routes";
import type { Profile } from "../api/types";
import { screenRendered } from "./a11y";
import { server } from "./server";

export interface Recorded {
  method: string;
  path: string;
  csrf: string | null;
  body: unknown;
}

/** Every request made to the fake API, in order. */
export const recorded: Recorded[] = [];

export function resetRecorded(): void {
  recorded.length = 0;
}

export const agent: Profile = {
  id: "11111111-1111-4111-8111-111111111111",
  fullName: "Ada Mwansa",
  email: "ada@example.test",
  membershipNumber: "AGT-0101",
  membershipClass: "Full",
  agency: { id: "22222222-2222-4222-8222-222222222222", name: "Kafue Properties", isActive: true },
  roles: ["RegisteredAgent"],
  hasCurrentPractisingCertificate: true,
  lastSignIn: null,
  recoveryCodesRemaining: 10,
  recoveryCodesLow: false,
};

export const admin: Profile = {
  id: "33333333-3333-4333-8333-333333333333",
  fullName: "Brian Phiri",
  email: "brian@example.test",
  membershipNumber: null,
  membershipClass: null,
  agency: null,
  roles: ["Admin"],
  hasCurrentPractisingCertificate: false,
  lastSignIn: null,
  recoveryCodesRemaining: 10,
  recoveryCodesLow: false,
};

export const problem = (status: number, title: string, detail?: string, extra: Record<string, unknown> = {}) =>
  HttpResponse.json({ status, title, ...(detail === undefined ? {} : { detail }), traceId: "trace-123", ...extra }, { status, headers: { "Content-Type": "application/problem+json" } });

/** The API's answers for someone fully signed in as the given profile. Other handlers are added per test. */
export function signedInAs(profile: Profile): void {
  server.use(
    http.get("/api/auth/session", () => HttpResponse.json({ userId: profile.id, twoFactorComplete: true })),
    http.get("/api/me", () => HttpResponse.json(profile)),
  );
}

export function signedOut(): void {
  server.use(http.get("/api/auth/session", () => problem(401, "Unauthorized")));
}

/** A fake API that keeps the log in `recorded`, and answers the anti-forgery token request. */
export function fakeApi(handlers: Record<string, (body: unknown) => JsonBodyType | Response | null>): void {
  let tokens = 0;
  const log = async (request: Request) => {
    const text = await request.clone().text();
    recorded.push({ method: request.method, path: new URL(request.url).pathname + new URL(request.url).search, csrf: request.headers.get("X-CSRF-TOKEN"), body: text === "" ? undefined : (JSON.parse(text) as unknown) });
  };

  server.use(
    http.get("/api/auth/csrf", async ({ request }) => {
      await log(request);
      tokens += 1;

      return HttpResponse.json({ token: `csrf-${tokens}` });
    }),
  );

  for (const [key, answer] of Object.entries(handlers)) {
    const [method = "", path = ""] = key.split(" ");
    server.use(
      http.all(path, async ({ request }) => {
        if (request.method !== method) {
          return undefined;
        }

        await log(request);
        const text = await request.clone().text();
        const result = answer(text === "" ? undefined : (JSON.parse(text) as unknown));
        if (result instanceof Response) {
          return result;
        }

        return result === null ? new HttpResponse(null, { status: 204 }) : HttpResponse.json(result);
      }),
    );
  }
}

export function renderApp(path = "/") {
  screenRendered.value = true; // checked for accessibility problems when the test ends (see setup.ts)
  const router = createTestRouter([path]);
  const view = render(<RouterProvider router={router} />);

  return { ...view, router };
}

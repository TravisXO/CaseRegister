import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";

// Every request the app makes in a test goes here, never to a real server. A request nothing has answered is an error, so a test
// cannot pass by accident while the app quietly calls something it should not.
//
// The one standing answer is an empty inbox, because the bell asks for its count whenever someone signed in moves to a page. A test about
// notifications puts its own answer in front of this one.
export const server = setupServer(
  http.get("/api/notifications", () => HttpResponse.json({ unreadCount: 0, needsAcknowledgementCount: 0, items: [] })),
  // What kind of deployment this is, for the demo notice. A test about it puts its own answer in front of this one.
  http.get("/api/deployment", () => HttpResponse.json({ mode: "Demo" })),
  // A member's profile page lists their practising certificates. None, unless a test says otherwise.
  http.get("/api/me/practising-certificates", () => HttpResponse.json([])),
);

/**
 * Every request nothing answered, in this test. A request nobody answers is refused (so the app sees a failed call), but an app can swallow a
 * failed call and the test would still pass, so the shared setup also fails the test that made it. A test that makes one on purpose
 * (see setup.test.ts) clears this list itself.
 */
export const unhandledRequests: string[] = [];

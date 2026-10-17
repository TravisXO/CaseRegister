import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";

// Every request the app makes in a test goes here, never to a real server. A request nothing has answered is an error, so a test
// cannot pass by accident while the app quietly calls something it should not.
//
// The one standing answer is an empty inbox, because the bell asks for its count whenever someone signed in moves to a page. A test about
// notifications puts its own answer in front of this one.
export const server = setupServer(http.get("/api/notifications", () => HttpResponse.json({ unreadCount: 0, needsAcknowledgementCount: 0, items: [] })));

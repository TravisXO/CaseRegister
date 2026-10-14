import { setupServer } from "msw/node";

// Every request the app makes in a test goes here, never to a real server. A request nothing has answered is an error, so a test
// cannot pass by accident while the app quietly calls something it should not.
export const server = setupServer();

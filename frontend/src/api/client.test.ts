import { http, HttpResponse } from "msw";
import { describe, expect, it, vi } from "vitest";
import { ApiError, describeError, forgetCsrfToken, onSessionEnded, refreshCsrfToken, request, waitMessage } from "./client";
import { fakeApi, problem, recorded, resetRecorded } from "../test/helpers";
import { server } from "../test/server";

const ok = () => ({ done: true });

describe("the anti-forgery token", () => {
  it.each(["POST", "PUT", "PATCH", "DELETE"])("is sent on every %s request", async (method) => {
    resetRecorded();
    fakeApi({ [`${method} /api/thing`]: ok });

    await request(method, "/api/thing", { body: {} });

    expect(recorded.find((call) => call.method === method)?.csrf).toBe("csrf-1");
  });

  it("is not fetched or sent for a GET", async () => {
    resetRecorded();
    fakeApi({ "GET /api/thing": ok });

    await request("GET", "/api/thing");

    expect(recorded.map((call) => call.path)).toEqual(["/api/thing"]);
    expect(recorded[0]?.csrf).toBeNull();
  });

  it("is fetched once and used again for the next unsafe request", async () => {
    resetRecorded();
    fakeApi({ "POST /api/thing": ok });

    await request("POST", "/api/thing", { body: {} });
    await request("POST", "/api/thing", { body: {} });

    expect(recorded.filter((call) => call.path === "/api/auth/csrf")).toHaveLength(1);
    expect(recorded.filter((call) => call.method === "POST").map((call) => call.csrf)).toEqual(["csrf-1", "csrf-1"]);
  });

  it("is fetched once when two unsafe requests start together", async () => {
    resetRecorded();
    fakeApi({ "POST /api/thing": ok });

    await Promise.all([request("POST", "/api/thing", { body: {} }), request("POST", "/api/thing", { body: {} })]);

    expect(recorded.filter((call) => call.path === "/api/auth/csrf")).toHaveLength(1);
  });

  it("is fetched again after signing in, and the new one is used", async () => {
    resetRecorded();
    fakeApi({ "POST /api/thing": ok });
    await request("POST", "/api/thing", { body: {} });

    await refreshCsrfToken(); // what signing in does
    await request("POST", "/api/thing", { body: {} });

    expect(recorded.filter((call) => call.method === "POST").map((call) => call.csrf)).toEqual(["csrf-1", "csrf-2"]);
  });

  it("is fetched again when the old one is refused, and the request is then sent once more", async () => {
    resetRecorded();
    let calls = 0;
    fakeApi({
      "POST /api/thing": () => {
        calls += 1;

        return calls === 1 ? problem(400, "Invalid request", "The anti-forgery token is missing or invalid.") : ok();
      },
    });

    await expect(request("POST", "/api/thing", { body: {} })).resolves.toEqual({ done: true });

    expect(recorded.filter((call) => call.method === "POST").map((call) => call.csrf)).toEqual(["csrf-1", "csrf-2"]);
  });

  it("is forgotten, so the next unsafe request fetches a new one", async () => {
    resetRecorded();
    fakeApi({ "POST /api/thing": ok });
    await request("POST", "/api/thing", { body: {} });

    forgetCsrfToken();
    await request("POST", "/api/thing", { body: {} });

    expect(recorded.filter((call) => call.path === "/api/auth/csrf")).toHaveLength(2);
  });
});

describe("requests", () => {
  it("send JSON, stay on this site and are never cached", async () => {
    fakeApi({ "POST /api/thing": ok });
    const spy = vi.spyOn(globalThis, "fetch");

    await request("POST", "/api/thing", { body: { name: "x" } });

    const init = spy.mock.calls.find((call) => call[0] === "/api/thing")?.[1];
    expect(init?.credentials).toBe("same-origin");
    expect(init?.cache).toBe("no-store");
    expect(init?.headers).toMatchObject({ "Content-Type": "application/json", Accept: "application/json" });
    expect(init?.body).toBe('{"name":"x"}');
  });
  it("send no body and no content type when there is no body", async () => {
    let contentType: string | null = "not seen";
    server.use(http.get("/api/thing", ({ request: incoming }) => {
      contentType = incoming.headers.get("Content-Type");

      return HttpResponse.json({});
    }));

    await request("GET", "/api/thing");

    expect(contentType).toBeNull();
  });

  it("treat a 204 as an empty answer", async () => {
    fakeApi({ "DELETE /api/thing": () => null });

    await expect(request("DELETE", "/api/thing")).resolves.toBeUndefined();
  });
});

describe("errors", () => {
  it("become an ApiError with the message and the trace id", async () => {
    server.use(http.get("/api/thing", () => problem(409, "Already decided", "This proposal has already been decided.")));

    const error = await request("GET", "/api/thing").catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(ApiError);
    expect(describeError(error)).toEqual({ message: "This proposal has already been decided.", traceId: "trace-123" });
  });

  it("show the title when there is no detail", async () => {
    server.use(http.get("/api/thing", () => problem(404, "Not found")));

    expect(describeError(await request("GET", "/api/thing").catch((caught: unknown) => caught))).toEqual({ message: "Not found", traceId: "trace-123" });
  });

  it("keep the field problems of a validation error", async () => {
    server.use(http.get("/api/thing", () => problem(400, "One or more validation errors occurred.", undefined, { errors: { Email: ["Enter an email address."] } })));

    const error = (await request("GET", "/api/thing").catch((caught: unknown) => caught)) as ApiError;

    expect(error.fieldErrors).toEqual({ Email: ["Enter an email address."] });
  });

  it("keep the rules that stopped an admin action", async () => {
    server.use(http.get("/api/thing", () => problem(422, "Not possible now", "A rule does not allow this.", { problems: [{ code: "LastAdmin", message: "There would be no admin left." }] })));

    const error = (await request("GET", "/api/thing").catch((caught: unknown) => caught)) as ApiError;

    expect(error.problems).toEqual([{ code: "LastAdmin", message: "There would be no admin left." }]);
  });

  it("never show anything from a response that is not a ProblemDetails", async () => {
    server.use(http.get("/api/thing", () => new HttpResponse("<html>Bad gateway: internal host 10.0.0.7 stack trace</html>", { status: 502 })));

    const message = describeError(await request("GET", "/api/thing").catch((caught: unknown) => caught));

    expect(JSON.stringify(message)).not.toContain("10.0.0.7");
    expect(JSON.stringify(message)).not.toContain("html");
  });

  it("say so when the server cannot be reached", async () => {
    server.use(http.get("/api/thing", () => HttpResponse.error()));

    const error = (await request("GET", "/api/thing").catch((caught: unknown) => caught)) as ApiError;

    expect(error.status).toBe(0);
    expect(describeError(error).message).toMatch(/could not reach the server/i);
  });

  it("show nothing of an error that is not the API's", () => {
    expect(describeError(new Error("secret internal detail"))).toEqual({ message: "Something went wrong. Try again." });
  });
});

describe("401, 403 and 429", () => {
  it("a 401 on an ordinary call says the session has ended", async () => {
    const ended = vi.fn();
    onSessionEnded(ended);
    server.use(http.get("/api/thing", () => problem(401, "Unauthorized")));

    await expect(request("GET", "/api/thing")).rejects.toBeInstanceOf(ApiError);

    expect(ended).toHaveBeenCalledOnce();
  });

  it("a 401 on a sign-in call is just a failed sign-in, not an ended session", async () => {
    const ended = vi.fn();
    onSessionEnded(ended);
    fakeApi({ "POST /api/auth/sign-in": () => problem(401, "Sign-in failed", "Sign-in failed. Check your details and try again.") });

    await expect(request("POST", "/api/auth/sign-in", { body: {}, unauthorised: "report" })).rejects.toBeInstanceOf(ApiError);

    expect(ended).not.toHaveBeenCalled();
  });

  it("a 401 also drops the anti-forgery token", async () => {
    resetRecorded();
    fakeApi({ "POST /api/thing": ok, "GET /api/other": () => problem(401, "Unauthorized") });
    await request("POST", "/api/thing", { body: {} });

    await request("GET", "/api/other").catch(() => undefined);
    await request("POST", "/api/thing", { body: {} });

    expect(recorded.filter((call) => call.path === "/api/auth/csrf")).toHaveLength(2);
  });

  it("a 403 is an error with that status and is not an ended session", async () => {
    const ended = vi.fn();
    onSessionEnded(ended);
    server.use(http.get("/api/thing", () => problem(403, "Forbidden")));

    const error = (await request("GET", "/api/thing").catch((caught: unknown) => caught)) as ApiError;

    expect(error.status).toBe(403);
    expect(ended).not.toHaveBeenCalled();
  });

  it("a 429 says when to try again, from Retry-After", async () => {
    server.use(http.get("/api/thing", () => HttpResponse.json({ status: 429, title: "Too many attempts", detail: "Too many sign-in attempts. Please wait and try again.", traceId: "t-9" }, { status: 429, headers: { "Retry-After": "42" } })));

    const error = (await request("GET", "/api/thing").catch((caught: unknown) => caught)) as ApiError;

    expect(error.retryAfterSeconds).toBe(42);
    expect(describeError(error)).toEqual({ message: "Too many attempts. Try again in 42 seconds.", traceId: "t-9" });
  });

  it.each([
    [1, "Too many attempts. Try again in 1 second."],
    [89, "Too many attempts. Try again in 89 seconds."],
    [90, "Too many attempts. Try again in 2 minutes."],
    [600, "Too many attempts. Try again in 10 minutes."],
    [undefined, "Too many attempts. Wait a little and try again."],
  ])("the wait of %s seconds is worded %s", (seconds, expected) => {
    expect(waitMessage(seconds)).toBe(expected);
  });
});

import axe from "axe-core";
import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { StartingUpNotice } from "../components/StartingUpNotice";
import { server } from "../test/server";
import { ApiError, request } from "./client";
import { AUTO_RETRY_MS, STARTING_UP_TITLE, clear } from "./startup";

const startingUp = () =>
  HttpResponse.json({ title: STARTING_UP_TITLE, status: 503 }, { status: 503, headers: { "Content-Type": "application/problem+json", "Retry-After": "30" } });

let calls: string[];

beforeEach(() => {
  calls = [];
  server.events.on("request:start", ({ request: made }) => {
    calls.push(`${made.method} ${new URL(made.url).pathname}`);
  });
  vi.useFakeTimers({ shouldAdvanceTime: true, toFake: ["setTimeout", "clearTimeout"] });
});

afterEach(() => {
  vi.useRealTimers();
  server.events.removeAllListeners();
  clear();
});

const advance = (ms: number) => act(async () => { await vi.advanceTimersByTimeAsync(ms); });

describe("when the server is starting up", () => {
  it("says so in a status message and asks again once for a read, after 30 seconds", async () => {
    let answers = 0;
    server.use(http.get("/api/me", () => (++answers === 1 ? startingUp() : HttpResponse.json({ id: "x" }))));
    render(<StartingUpNotice />);

    const pending = request<{ id: string }>("GET", "/api/me");
    const outcome = pending.then((value) => value, (error: unknown) => error);

    const status = await screen.findByRole("status");
    expect(status).toHaveTextContent("The server is starting up.");
    expect(status).toHaveTextContent("ask again by itself in 30 seconds");
    expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
    expect(calls).toEqual(["GET /api/me"]);

    await advance(AUTO_RETRY_MS - 1000);
    expect(calls).toEqual(["GET /api/me"]); // not yet

    await advance(2000);
    expect(await outcome).toEqual({ id: "x" });
    expect(calls).toEqual(["GET /api/me", "GET /api/me"]); // once more, and no more
    expect(screen.queryByRole("status")).not.toBeInTheDocument(); // and the message goes once the server has answered
  });

  it("asks a read again only once: if it is still starting after that, the caller is told", async () => {
    server.use(http.get("/api/me", () => startingUp()));
    render(<StartingUpNotice />);

    const outcome = request("GET", "/api/me").then(() => null, (error: unknown) => error);
    await advance(AUTO_RETRY_MS + 1000);
    const error = await outcome;

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(503);
    await advance(10 * AUTO_RETRY_MS);
    expect(calls).toEqual(["GET /api/me", "GET /api/me"]); // never a third
    expect(screen.getByRole("status")).toHaveTextContent("not confirmed");
  });

  it.each(["POST", "PUT", "PATCH", "DELETE"])("never asks a %s again by itself, however long it waits", async (method) => {
    server.use(
      http.get("/api/auth/csrf", () => HttpResponse.json({ token: "t" })),
      http.all("/api/things", () => startingUp()),
    );
    render(<StartingUpNotice />);

    const error = await request(method, "/api/things", { body: { a: 1 } }).then(() => null, (failure: unknown) => failure);
    await advance(20 * AUTO_RETRY_MS);

    expect((error as ApiError).status).toBe(503);
    expect(calls.filter((call) => call.endsWith("/api/things"))).toEqual([`${method} /api/things`]); // once, by the person; never again by the page
    expect(await screen.findByRole("status")).toHaveTextContent("not confirmed");
    expect(screen.getByRole("status")).toHaveTextContent("do it again");
  });

  it("goes away as soon as any request is answered", async () => {
    let up = false;
    server.use(http.get("/api/me", () => (up ? HttpResponse.json({}) : startingUp())), http.get("/api/other", () => HttpResponse.json({})));
    render(<StartingUpNotice />);
    void request("GET", "/api/me").catch(() => undefined);
    await screen.findByRole("status");

    up = true;
    await request("GET", "/api/other");

    expect(screen.queryByRole("status")).not.toBeInTheDocument();
    await advance(2 * AUTO_RETRY_MS); // and the read that was waiting is still asked again, once
  });

  it("has a Retry button that checks the server and sends a waiting read at once when it is up", async () => {
    let answers = 0;
    server.use(
      http.get("/api/me", () => (++answers === 1 ? startingUp() : HttpResponse.json({ id: "x" }))),
      http.get("/api/auth/csrf", () => HttpResponse.json({ token: "t" })),
    );
    render(<StartingUpNotice />);
    const outcome = request<{ id: string }>("GET", "/api/me").then((value) => value, (error: unknown) => error);
    await screen.findByRole("status");

    await userEvent.click(screen.getByRole("button", { name: "Retry" }));

    expect(await outcome).toEqual({ id: "x" }); // without waiting the 30 seconds
    expect(calls).toEqual(["GET /api/me", "GET /api/auth/csrf", "GET /api/me"]);
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });

  it("says when the server is still not ready after Retry, and keeps the message", async () => {
    server.use(http.get("/api/me", () => startingUp()), http.get("/api/auth/csrf", () => startingUp()));
    render(<StartingUpNotice />);
    void request("GET", "/api/me").catch(() => undefined);
    await screen.findByRole("status");

    await userEvent.click(screen.getByRole("button", { name: "Retry" }));

    expect(await screen.findByText(/still starting/)).toBeInTheDocument();
    expect(screen.getByRole("status")).toBeInTheDocument();
    await advance(5 * AUTO_RETRY_MS);
  });

  it("passes an accessibility check", async () => {
    server.use(http.get("/api/me", () => startingUp()));
    const { container } = render(<StartingUpNotice />);
    void request("GET", "/api/me").catch(() => undefined);
    await screen.findByRole("status");

    const result = await axe.run(container, { rules: { "color-contrast": { enabled: false } } });

    expect(result.violations.map((violation) => `${violation.id}: ${violation.help}`)).toEqual([]);
    await advance(2 * AUTO_RETRY_MS);
  });
});

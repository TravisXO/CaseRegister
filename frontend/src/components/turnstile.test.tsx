import { render, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";
import { problem, renderApp } from "../test/helpers";
import { server } from "../test/server";
import { Turnstile } from "./Turnstile";

afterEach(() => {
  vi.restoreAllMocks();
});

describe("the check on each form", () => {
  it("asks Cloudflare for a token made for that form, by name", async () => {
    const rendered: Record<string, unknown>[] = [];
    window.turnstile = {
      render: (_container, options) => {
        rendered.push(options);

        return "widget";
      },
      reset: () => undefined,
      remove: () => undefined,
    };

    render(<Turnstile action="sign-up" onToken={() => undefined} />);

    await waitFor(() => {
      expect(rendered).toHaveLength(1);
    });
    expect(rendered[0]).toMatchObject({ action: "sign-up", sitekey: "test-site-key" });
  });

  it("is asked for as sign-in on the sign-in page and sign-up on the sign-up page", async () => {
    const actions: unknown[] = [];
    window.turnstile = {
      render: (_container, options) => {
        actions.push(options.action);
        (options.callback as (token: string) => void)("token-1");

        return "widget";
      },
      reset: () => undefined,
      remove: () => undefined,
    };
    server.use(http.get("/api/auth/session", () => problem(401, "Unauthorized")));

    renderApp("/sign-in");
    await screen.findByRole("button", { name: "Sign in" });
    expect(actions).toEqual(["sign-in"]);
  });

  it("names the sign-up form's check on the sign-up page", async () => {
    const actions: unknown[] = [];
    window.turnstile = {
      render: (_container, options) => {
        actions.push(options.action);

        return "widget";
      },
      reset: () => undefined,
      remove: () => undefined,
    };
    server.use(http.get("/api/auth/session", () => HttpResponse.json({}, { status: 401 })));

    renderApp("/sign-up");
    await screen.findByRole("heading", { name: /apply|sign up|join/i });
    expect(actions).toEqual(["sign-up"]);
  });
});

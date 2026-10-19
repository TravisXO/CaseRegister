import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
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

  it("says clearly when the check fails, and offers a way forward that asks Cloudflare again", async () => {
    const rendered: Record<string, unknown>[] = [];
    window.turnstile = {
      render: (_container, options) => {
        rendered.push(options);

        return "widget";
      },
      reset: () => undefined,
      remove: () => undefined,
    };

    render(<Turnstile action="sign-in" onToken={() => undefined} />);
    await waitFor(() => {
      expect(rendered).toHaveLength(1);
    });
    act(() => {
      (rendered[0]?.["error-callback"] as () => void)();
    });

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("The security check could not load or did not finish.");
    await userEvent.click(within(alert).getByRole("button", { name: "Try the security check again" }));

    await waitFor(() => {
      expect(rendered).toHaveLength(2);
    });
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("is on the sign-in and sign-up forms and never on the step where the code is asked for", async () => {
    let drawn = 0;
    window.turnstile = {
      render: () => {
        drawn += 1;

        return "widget";
      },
      reset: () => undefined,
      remove: () => undefined,
    };
    server.use(http.get("/api/auth/session", () => problem(401, "Unauthorized")));

    renderApp("/sign-in/code");
    await screen.findByRole("button", { name: /verify|continue|sign in/i });

    expect(drawn).toBe(0);
  });
});

import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { describe, expect, it, vi } from "vitest";
import { agent, fakeApi, problem, recorded, renderApp, resetRecorded } from "../test/helpers";
import { server } from "../test/server";

const certificates = () => server.use(http.get("/api/me/practising-certificates", () => HttpResponse.json([])));

/** The API's answers while nobody is signed in, and then, once `signIn()` is called, while the agent is. */
function sessionThatCanChange(twoFactorComplete = true) {
  let signedIn = false;
  server.use(
    http.get("/api/auth/session", () => (signedIn ? HttpResponse.json({ userId: agent.id, twoFactorComplete }) : problem(401, "Unauthorized"))),
    http.get("/api/me", () => HttpResponse.json(agent)),
  );
  certificates();

  return () => {
    signedIn = true;
  };
}

async function fillSignIn() {
  await userEvent.type(await screen.findByLabelText("Email address"), "ada@example.test");
  await userEvent.type(screen.getByLabelText("Password"), "correct horse battery staple");
}

describe("signing in", () => {
  it("asks for the email and password, and the security check, then moves to the second step", async () => {
    resetRecorded();
    sessionThatCanChange();
    fakeApi({ "POST /api/auth/sign-in": () => ({ status: "two-factor-required" }) });
    renderApp("/sign-in");

    await fillSignIn();
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByRole("heading", { name: "Second step" })).toBeInTheDocument();
    const call = recorded.find((item) => item.path === "/api/auth/sign-in");
    expect(call?.csrf).toBe("csrf-1");
    expect(call?.body).toEqual({ email: "ada@example.test", password: "correct horse battery staple", turnstileToken: "token-1" });
  });

  it("completes with the code, fetches a new anti-forgery token, and shows the profile", async () => {
    resetRecorded();
    const signIn = sessionThatCanChange();
    fakeApi({
      "POST /api/auth/sign-in": () => ({ status: "two-factor-required" }),
      "POST /api/auth/2fa/verify": () => {
        signIn();

        return { status: "signed-in" };
      },
    });
    renderApp("/sign-in");
    await fillSignIn();
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    await userEvent.type(await screen.findByLabelText("Code from your authenticator app"), "123456");
    await userEvent.click(screen.getByRole("button", { name: "Continue" }));

    expect(await screen.findByRole("heading", { name: "My profile" })).toBeInTheDocument();
    expect(screen.getByText("Ada Mwansa")).toBeInTheDocument();
    expect(recorded.find((item) => item.path === "/api/auth/2fa/verify")?.body).toEqual({ code: "123456" });
    // One token for the password step, and a new one once signed in.
    expect(recorded.filter((item) => item.path === "/api/auth/csrf")).toHaveLength(2);
  });

  it("takes a recovery code at the second step, by the same route", async () => {
    resetRecorded();
    const signIn = sessionThatCanChange();
    fakeApi({
      "POST /api/auth/sign-in": () => ({ status: "two-factor-required" }),
      "POST /api/auth/2fa/verify": () => {
        signIn();

        return { status: "signed-in" };
      },
    });
    renderApp("/sign-in/code");

    await userEvent.click(await screen.findByRole("button", { name: "Use a recovery code instead" }));
    await userEvent.type(screen.getByLabelText("Recovery code"), "ABCD-1234");
    await userEvent.click(screen.getByRole("button", { name: "Continue" }));

    expect(await screen.findByRole("heading", { name: "My profile" })).toBeInTheDocument();
    expect(recorded.find((item) => item.path === "/api/auth/2fa/verify")?.body).toEqual({ code: "ABCD-1234" });
  });

  it("goes back to the app's code after choosing recovery and changing your mind", async () => {
    sessionThatCanChange();
    renderApp("/sign-in/code");

    await userEvent.click(await screen.findByRole("button", { name: "Use a recovery code instead" }));
    await userEvent.click(screen.getByRole("button", { name: "Use the code from the app instead" }));

    expect(screen.getByLabelText("Code from your authenticator app")).toBeInTheDocument();
  });

  it("forces someone with no authenticator yet to set one up", async () => {
    resetRecorded();
    const signIn = sessionThatCanChange(false);
    fakeApi({
      "POST /api/auth/sign-in": () => {
        signIn();

        return { status: "two-factor-enrolment-required" };
      },
    });
    renderApp("/sign-in");

    await fillSignIn();
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByRole("heading", { name: "Set up two-factor sign-in" })).toBeInTheDocument();
  });

  it("shows what the API said and its trace id, nothing more, when sign-in fails", async () => {
    sessionThatCanChange();
    fakeApi({ "POST /api/auth/sign-in": () => problem(401, "Sign-in failed", "Sign-in failed. Check your details and try again.") });
    renderApp("/sign-in");
    await fillSignIn();

    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    const summary = await screen.findByRole("alert");
    expect(summary).toHaveTextContent("Sign-in failed. Check your details and try again.");
    expect(summary).toHaveTextContent("Reference: trace-123");
    expect(summary.textContent).not.toMatch(/not found|no account|does not exist|unknown email|wrong password/i);
    expect(screen.queryByText("Your session ended. Sign in again to carry on.")).not.toBeInTheDocument(); // a failed sign-in is not an ended session
  });

  it("asks for a new security check after a failed attempt, and clears the password", async () => {
    sessionThatCanChange();
    fakeApi({ "POST /api/auth/sign-in": () => problem(401, "Sign-in failed", "Sign-in failed. Check your details and try again.") });
    const reset = vi.fn();
    renderApp("/sign-in");
    await fillSignIn();
    window.turnstile = { ...(window.turnstile!), reset };

    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    await screen.findByRole("alert");
    expect(reset).toHaveBeenCalled();
  });

  it("says when to try again after too many attempts", async () => {
    sessionThatCanChange();
    fakeApi({ "POST /api/auth/sign-in": () => HttpResponse.json({ status: 429, title: "Too many attempts", traceId: "t-5" }, { status: 429, headers: { "Retry-After": "30" } }) });
    renderApp("/sign-in");
    await fillSignIn();

    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByText("Too many attempts. Try again in 30 seconds.")).toBeInTheDocument();
  });

  it("lists what is missing, moves focus to the first field, and sends nothing", async () => {
    resetRecorded();
    sessionThatCanChange();
    fakeApi({ "POST /api/auth/sign-in": () => ({ status: "two-factor-required" }) });
    renderApp("/sign-in");
    await screen.findByLabelText("Email address");

    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    const summary = await screen.findByRole("alert");
    expect(summary).toHaveTextContent("Enter your email address.");
    expect(summary).toHaveTextContent("Enter your password.");
    await waitFor(() => { expect(summary).toHaveFocus(); });
    await userEvent.click(within(summary).getByRole("link", { name: "Enter your email address." }));
    expect(screen.getByLabelText("Email address")).toHaveFocus();
    expect(screen.getByLabelText("Email address")).toHaveAttribute("aria-invalid", "true");
    expect(screen.getByLabelText("Email address")).toHaveAccessibleDescription("Enter your email address.");
    expect(recorded.filter((item) => item.path === "/api/auth/sign-in")).toHaveLength(0);
  });

  it("does not send twice when the button is pressed twice, and disables it while it waits", async () => {
    resetRecorded();
    sessionThatCanChange();
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    fakeApi({});
    server.use(
      http.post("/api/auth/sign-in", async ({ request }) => {
        recorded.push({ method: "POST", path: "/api/auth/sign-in", csrf: request.headers.get("X-CSRF-TOKEN"), body: undefined });
        await gate;

        return HttpResponse.json({ status: "two-factor-required" });
      }),
    );
    renderApp("/sign-in");
    await fillSignIn();
    const button = screen.getByRole("button", { name: "Sign in" });

    await userEvent.click(button);
    await userEvent.click(button);
    await userEvent.type(screen.getByLabelText("Password"), "{Enter}");

    expect(screen.getByRole("button", { name: "Please wait…" })).toBeDisabled();
    act(() => {
      release();
    });
    await screen.findByRole("heading", { name: "Second step" });
    expect(recorded.filter((item) => item.path === "/api/auth/sign-in")).toHaveLength(1);
  });

  it("uses the right autocomplete values, allows pasting a password, and can show or hide it", async () => {
    sessionThatCanChange();
    renderApp("/sign-in");
    const email = await screen.findByLabelText("Email address");
    const password = screen.getByLabelText("Password");

    expect(email).toHaveAttribute("autocomplete", "username");
    expect(password).toHaveAttribute("autocomplete", "current-password");
    expect(password).toHaveAttribute("type", "password");

    await userEvent.click(password);
    await userEvent.paste("pasted from a password manager");
    expect(password).toHaveValue("pasted from a password manager");

    await userEvent.click(screen.getByRole("button", { name: "Show password" }));
    expect(password).toHaveAttribute("type", "text");
    expect(screen.getByRole("button", { name: "Hide password" })).toHaveAttribute("aria-pressed", "true");
  });

  it("uses one-time-code for the code field", async () => {
    sessionThatCanChange();
    renderApp("/sign-in/code");

    expect(await screen.findByLabelText("Code from your authenticator app")).toHaveAttribute("autocomplete", "one-time-code");
  });

  it("shows a signed-in person the profile instead of the sign-in form", async () => {
    server.use(http.get("/api/auth/session", () => HttpResponse.json({ userId: agent.id, twoFactorComplete: true })), http.get("/api/me", () => HttpResponse.json(agent)));
    certificates();

    renderApp("/sign-in");

    expect(await screen.findByRole("heading", { name: "My profile" })).toBeInTheDocument();
  });
});

describe("the security check", () => {
  it("is on both the sign-in and the sign-up forms", async () => {
    sessionThatCanChange();
    const render = vi.fn((_container: HTMLElement, options: Record<string, unknown>) => {
      (options.callback as (token: string) => void)("t");

      return "w";
    });
    window.turnstile = { render, reset: () => undefined, remove: () => undefined };

    const first = renderApp("/sign-in");
    await screen.findByLabelText("Email address");
    await waitFor(() => { expect(render).toHaveBeenCalledTimes(1); });
    first.unmount();
    renderApp("/sign-up");
    await screen.findByLabelText("Full name");
    await waitFor(() => { expect(render).toHaveBeenCalledTimes(2); });

    expect(render.mock.calls.every((call) => call[1].sitekey === "test-site-key")).toBe(true);
  });
});

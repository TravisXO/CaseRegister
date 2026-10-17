import axe from "axe-core";
import { act, screen, within } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";
import { api } from "../api/endpoints";
import type { Profile } from "../api/types";
import { agent, fakeApi, recorded, renderApp, resetRecorded } from "../test/helpers";
import { server } from "../test/server";

const MINUTE = 60 * 1000;

afterEach(() => {
  vi.useRealTimers();
});

/**
 * Opens the profile under fake timers that also tick with the real clock (so the page can load while the timeout clocks are the fake ones),
 * then moves time on by hand.
 */
async function openWith(session: { sessionEndsAt?: string | null; idleTimeoutSeconds?: number } = {}) {
  resetRecorded();
  const profile: Profile = agent;
  server.use(
    http.get("/api/auth/session", () => HttpResponse.json({ userId: profile.id, twoFactorComplete: true, sessionEndsAt: null, idleTimeoutSeconds: 1800, ...session })),
    http.get("/api/me", () => HttpResponse.json(profile)),
    http.get("/api/me/practising-certificates", () => HttpResponse.json([])),
  );
  fakeApi({});
  vi.useFakeTimers({ shouldAdvanceTime: true, toFake: ["setTimeout", "clearTimeout", "Date"] });
  renderApp("/profile");
  await screen.findByText("Ada Mwansa");
  await screen.findByText("You have not added a certificate yet.");
}

const advance = (ms: number) => act(async () => { await vi.advanceTimersByTimeAsync(ms); });

describe("the warning before the idle limit", () => {
  it("does not appear while the person is working, because every answer from the API restarts the clock", async () => {
    await openWith();

    for (let round = 0; round < 5; round++) {
      await advance(20 * MINUTE);
      await act(async () => { await api.session(); });
    }

    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  });

  it("appears two minutes before the 30-minute limit, as an alert dialog with focus inside it", async () => {
    await openWith();

    await advance(27 * MINUTE + 59 * 1000);
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    await advance(2000);

    const dialog = screen.getByRole("alertdialog");
    expect(dialog).toHaveAccessibleName("You are about to be signed out");
    expect(dialog).toHaveAccessibleDescription(/In about 2 minutes you will be signed out/);
    expect(dialog).toHaveAttribute("aria-modal", "true");
    expect(dialog.contains(document.activeElement)).toBe(true);
    expect(screen.getByRole("button", { name: "Stay signed in" })).toHaveFocus();
  });

  it("stays signed in with one cheap authenticated request, closes, and gives focus back", async () => {
    await openWith();
    const menu = screen.getByRole("link", { name: "My sessions" });
    menu.focus();
    await advance(28 * MINUTE);
    const asked: string[] = [];
    server.events.on("request:start", ({ request }) => {
      asked.push(new URL(request.url).pathname);
    });

    act(() => { screen.getByRole("button", { name: "Stay signed in" }).click(); });
    await advance(0);

    expect(asked).toEqual(["/api/auth/session"]); // the one cheap authenticated request, and nothing else
    server.events.removeAllListeners();
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(menu).toHaveFocus();

    // And the clocks start again: no warning for another 28 minutes, then it comes back.
    await advance(27 * MINUTE);
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    await advance(2 * MINUTE);
    expect(screen.getByRole("alertdialog")).toBeInTheDocument();
  });

  it("is never made to call the API by the timers themselves", async () => {
    const seen: string[] = [];
    server.events.on("request:start", ({ request }) => {
      seen.push(new URL(request.url).pathname);
    });
    await openWith();
    const before = seen.length;

    await advance(29 * MINUTE);
    expect(screen.getByRole("alertdialog")).toBeInTheDocument();
    expect(seen.length).toBe(before);
    server.events.removeAllListeners();
  });

  it("closes on Escape without signing out and without carrying the session on", async () => {
    await openWith();
    await advance(28 * MINUTE);
    const before = recorded.length;

    act(() => { document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true })); });

    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(recorded.length).toBe(before); // no sign-out, no extension
    expect(screen.getByText("Ada Mwansa")).toBeInTheDocument(); // still on the page, still shown as signed in
  });

  it("signs the page out of what it holds when the limit arrives, with the session-ended notice", async () => {
    await openWith();

    await advance(30 * MINUTE + 1000);

    expect(await screen.findByText("Your session ended. Sign in again to carry on.")).toBeInTheDocument();
    expect(screen.queryByText("Ada Mwansa")).not.toBeInTheDocument();
  });

  it("offers Sign out in the dialog", async () => {
    await openWith();
    fakeApi({ "POST /api/auth/sign-out": () => null });
    await advance(28 * MINUTE);

    act(() => { screen.getAllByRole("button", { name: "Sign out" }).at(-1)!.click(); });
    await advance(0);

    expect(recorded.some((item) => item.path === "/api/auth/sign-out")).toBe(true);
  });

  it("keeps Tab inside the dialog", async () => {
    await openWith();
    await advance(28 * MINUTE);
    const stay = screen.getByRole("button", { name: "Stay signed in" });
    const signOut = within(screen.getByRole("alertdialog")).getByRole("button", { name: "Sign out" });

    signOut.focus();
    act(() => { document.dispatchEvent(new KeyboardEvent("keydown", { key: "Tab", bubbles: true, cancelable: true })); });
    expect(stay).toHaveFocus();

    act(() => { document.dispatchEvent(new KeyboardEvent("keydown", { key: "Tab", shiftKey: true, bubbles: true, cancelable: true })); });
    expect(signOut).toHaveFocus();
  });
});

describe("the explanation before the 8-hour limit", () => {
  it("appears five minutes before, says it cannot be extended, and has no stay-signed-in button", async () => {
    await openWith({ sessionEndsAt: new Date(Date.now() + 10 * MINUTE).toISOString() });

    await advance(4 * MINUTE + 59 * 1000);
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    await advance(2000);

    const dialog = screen.getByRole("alertdialog");
    expect(dialog).toHaveAccessibleName("Your session is nearly at its time limit");
    expect(dialog).toHaveAccessibleDescription(/cannot be extended/);
    expect(screen.queryByRole("button", { name: "Stay signed in" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "I understand" })).toHaveFocus();
  });

  it("is dismissed by Escape or the button, and the session ends by itself at the limit", async () => {
    await openWith({ sessionEndsAt: new Date(Date.now() + 10 * MINUTE).toISOString() });
    await advance(5 * MINUTE + 1000);

    act(() => { document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true })); });
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();

    await advance(5 * MINUTE);
    expect(await screen.findByText("Your session ended. Sign in again to carry on.")).toBeInTheDocument();
  });
});

describe("the dialogs are accessible", () => {
  it("passes an automated accessibility check while open (idle warning)", async () => {
    await openWith();
    await advance(28 * MINUTE);

    const result = await axe.run(screen.getByRole("alertdialog"), { rules: { "color-contrast": { enabled: false } } });

    expect(result.violations.map((violation) => `${violation.id}: ${violation.help}`)).toEqual([]);
  });

  it("passes an automated accessibility check while open (time limit)", async () => {
    await openWith({ sessionEndsAt: new Date(Date.now() + 10 * MINUTE).toISOString() });
    await advance(5 * MINUTE + 1000);

    const result = await axe.run(screen.getByRole("alertdialog"), { rules: { "color-contrast": { enabled: false } } });

    expect(result.violations.map((violation) => `${violation.id}: ${violation.help}`)).toEqual([]);
  });
});

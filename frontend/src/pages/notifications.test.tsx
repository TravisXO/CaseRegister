import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { describe, expect, it, vi } from "vitest";
import type { AppNotification } from "../api/types";
import { admin, agent, fakeApi, recorded, renderApp, signedInAs } from "../test/helpers";
import { server } from "../test/server";

const item = (overrides: Partial<AppNotification> = {}): AppNotification => ({
  id: "aaaaaaaa-0000-4000-8000-000000000001",
  type: "ProposalWaiting",
  severity: "Info",
  text: "A proposal to suspend an account is waiting for you to confirm or reject it.",
  link: "/admin/proposals/bbbbbbbb-0000-4000-8000-000000000002",
  createdAt: "2026-10-13T08:05:00Z",
  readAt: null,
  acknowledgedAt: null,
  needsAcknowledgement: false,
  ...overrides,
});

const critical = item({
  id: "aaaaaaaa-0000-4000-8000-000000000009",
  type: "BreakGlass",
  severity: "Critical",
  text: "A break-glass action was run from the command line: a two-factor reset. It needs your acknowledgement.",
  link: "/admin/audit",
  needsAcknowledgement: true,
});

function inbox(items: AppNotification[]) {
  const open = (notification: AppNotification) => (notification.needsAcknowledgement ? !notification.acknowledgedAt : !notification.readAt);

  return () => HttpResponse.json({ unreadCount: items.filter(open).length, needsAcknowledgementCount: items.filter((n) => n.needsAcknowledgement && !n.acknowledgedAt).length, items });
}

describe("the bell", () => {
  it("names its count for a screen reader and shows it", async () => {
    signedInAs(admin);
    server.use(http.get("/api/notifications", inbox([item(), item({ id: "aaaaaaaa-0000-4000-8000-000000000003" }), critical])));

    renderApp("/profile");

    const bell = await screen.findByRole("link", { name: "Notifications, 3 unread" });
    expect(bell).toHaveAttribute("href", "/notifications");
    expect(bell).toHaveTextContent("3");
  });

  it("says zero plainly when there is nothing", async () => {
    signedInAs(agent);

    renderApp("/profile");

    expect(await screen.findByRole("link", { name: "Notifications, 0 unread" })).toBeInTheDocument();
  });

  it("is not there for someone who is not signed in", async () => {
    server.use(http.get("/api/auth/session", () => HttpResponse.json({ title: "Unauthorized", status: 401 }, { status: 401 })));

    renderApp("/sign-in");

    await screen.findByRole("heading", { name: "Sign in" });
    expect(screen.queryByRole("link", { name: /Notifications/ })).not.toBeInTheDocument();
  });

  it("asks for the count when the person moves to another page, and not when nothing happens", async () => {
    signedInAs(admin);
    let asked = 0;
    server.use(
      http.get("/api/notifications", () => {
        asked += 1;

        return HttpResponse.json({ unreadCount: asked, needsAcknowledgementCount: 0, items: [] });
      }),
      http.get("/api/auth/sessions", () => HttpResponse.json([])),
    );
    const view = renderApp("/profile");
    await screen.findByRole("link", { name: "Notifications, 1 unread" });

    await userEvent.click(await screen.findByRole("link", { name: "My sessions" }));

    expect(await screen.findByRole("link", { name: "Notifications, 2 unread" })).toBeInTheDocument();
    expect(view.router.state.location.pathname).toBe("/sessions");
  });

  it("is reached from the keyboard alone", async () => {
    signedInAs(admin);
    renderApp("/profile");
    const bell = await screen.findByRole("link", { name: /Notifications/ });

    bell.focus();
    await userEvent.keyboard("{Enter}");

    expect(await screen.findByRole("heading", { name: "Notifications" })).toBeInTheDocument();
  });

  it("is not read out when it changes because the page moved on, only after the person acts", async () => {
    signedInAs(admin);
    let items = [item(), item({ id: "aaaaaaaa-0000-4000-8000-000000000003" })];
    server.use(http.get("/api/notifications", () => inbox(items)()), http.get("/api/auth/sessions", () => HttpResponse.json([])));
    fakeApi({
      "POST /api/notifications/aaaaaaaa-0000-4000-8000-000000000001/read": () => {
        items = [{ ...items[0]!, readAt: "2026-10-13T09:00:00Z" }, items[1]!];

        return null;
      },
    });
    renderApp("/profile");
    await screen.findByRole("link", { name: "Notifications, 2 unread" });
    const region = screen.getAllByRole("status").find((element) => element.classList.contains("sr-only"))!;

    await userEvent.click(screen.getByRole("link", { name: "My sessions" }));
    await screen.findByRole("heading", { name: "My sessions" });
    expect(region).toHaveTextContent(""); // the page moved on and nothing was said

    await userEvent.click(screen.getByRole("link", { name: /Notifications/ }));
    await userEvent.click((await screen.findAllByRole("button", { name: "Mark as read" }))[0]!);

    await waitFor(() => { expect(region).toHaveTextContent("1 unread notification"); });
    expect(screen.getByRole("link", { name: "Notifications, 1 unread" })).toBeInTheDocument();
  });

  it("makes no request on a timer: hours pass and the API is asked nothing", async () => {
    signedInAs(admin);
    const seen: string[] = [];
    server.events.on("request:start", ({ request }) => {
      seen.push(new URL(request.url).pathname);
    });
    renderApp("/profile");
    await screen.findByRole("link", { name: "Notifications, 0 unread" });
    await waitFor(() => { expect(seen).toContain("/api/notifications"); });
    const before = seen.length;

    vi.useFakeTimers();
    try {
      await act(async () => {
        await vi.advanceTimersByTimeAsync(5 * 60 * 60 * 1000);
      });
    } finally {
      vi.useRealTimers();
    }

    expect(seen.length).toBe(before);
    server.events.removeAllListeners();
  });

  it("forgets the count when the person signs out", async () => {
    signedInAs(admin);
    server.use(http.get("/api/notifications", inbox([item()])));
    fakeApi({ "POST /api/auth/sign-out": () => null });
    renderApp("/profile");
    await screen.findByRole("link", { name: "Notifications, 1 unread" });

    await userEvent.click(screen.getByRole("button", { name: "Sign out" }));

    await screen.findByRole("heading", { name: "Sign in" });
    expect(screen.queryByRole("link", { name: /Notifications/ })).not.toBeInTheDocument();
  });
});

describe("the notifications page", () => {
  it("lists each notification with its severity in words, its time in Zambian time and a link", async () => {
    signedInAs(admin);
    server.use(http.get("/api/notifications", inbox([item(), critical])));

    renderApp("/notifications");

    expect(await screen.findByText("A proposal to suspend an account is waiting for you to confirm or reject it.")).toBeInTheDocument();
    const list = screen.getByRole("list");
    expect(within(list).getAllByRole("listitem")).toHaveLength(2);
    expect(within(list).getByText(/^Information/)).toBeInTheDocument();
    expect(within(list).getByText(/^Critical/)).toBeInTheDocument();
    expect(within(list).getAllByText(/Zambian time/)).toHaveLength(2);
    expect(within(list).getAllByRole("link", { name: "Open" }).map((link) => link.getAttribute("href"))).toEqual(["/admin/proposals/bbbbbbbb-0000-4000-8000-000000000002", "/admin/audit"]);
    expect(screen.getByTestId("unread-summary")).toHaveTextContent("2 unread notifications, 1 waiting to be acknowledged.");
  });

  it("follows a link inside the application and never one that leaves it", async () => {
    signedInAs(admin);
    server.use(http.get("/api/notifications", inbox([item({ link: "//evil.example/x" }), item({ id: "aaaaaaaa-0000-4000-8000-000000000003", link: "\\\\evil.example" })])));

    renderApp("/notifications");

    await screen.findAllByText(/waiting for you/);
    expect(screen.queryByRole("link", { name: "Open" })).not.toBeInTheDocument();
  });

  it("marks one read, with the anti-forgery token, and refreshes the count", async () => {
    signedInAs(admin);
    let items = [item()];
    server.use(http.get("/api/notifications", () => inbox(items)()));
    fakeApi({
      "POST /api/notifications/aaaaaaaa-0000-4000-8000-000000000001/read": () => {
        items = [{ ...items[0]!, readAt: "2026-10-13T09:00:00Z" }];

        return null;
      },
    });
    renderApp("/notifications");

    await userEvent.click(await screen.findByRole("button", { name: "Mark as read" }));

    await waitFor(() => { expect(screen.queryByRole("button", { name: "Mark as read" })).not.toBeInTheDocument(); });
    const call = recorded.find((entry) => entry.path.endsWith("/read"))!;
    expect(call.method).toBe("POST");
    expect(call.csrf).toMatch(/^csrf-/);
    expect(await screen.findByRole("link", { name: "Notifications, 0 unread" })).toBeInTheDocument();
  });

  it("marks all read but leaves a critical one waiting for its acknowledgement", async () => {
    signedInAs(admin);
    let items = [item(), item({ id: "aaaaaaaa-0000-4000-8000-000000000003" }), critical];
    server.use(http.get("/api/notifications", () => inbox(items)()));
    fakeApi({
      "POST /api/notifications/read-all": () => {
        items = items.map((n) => (n.needsAcknowledgement ? n : { ...n, readAt: "2026-10-13T09:00:00Z" }));

        return null;
      },
    });
    renderApp("/notifications");

    await userEvent.click(await screen.findByRole("button", { name: "Mark all as read" }));

    expect(await screen.findByRole("link", { name: "Notifications, 1 unread" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Acknowledge" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Mark all as read" })).not.toBeInTheDocument();
  });

  it("offers Acknowledge, and not Mark as read, for a critical notification, and sends the acknowledgement", async () => {
    signedInAs(admin);
    let items = [critical];
    server.use(http.get("/api/notifications", () => inbox(items)()));
    fakeApi({
      "POST /api/notifications/aaaaaaaa-0000-4000-8000-000000000009/acknowledge": () => {
        items = [{ ...critical, acknowledgedAt: "2026-10-13T09:00:00Z", readAt: "2026-10-13T09:00:00Z" }];

        return null;
      },
    });
    renderApp("/notifications");
    expect(await screen.findByRole("button", { name: "Acknowledge" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Mark as read" })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Acknowledge" }));

    expect(await screen.findByText(/^Acknowledged /)).toBeInTheDocument();
    expect(recorded.some((entry) => entry.method === "POST" && entry.path.endsWith("/acknowledge") && entry.csrf !== null)).toBe(true);
    expect(await screen.findByRole("link", { name: "Notifications, 0 unread" })).toBeInTheDocument();
  });

  it("keeps everything on the page however long it stays open: nothing dismisses itself", async () => {
    signedInAs(admin);
    server.use(http.get("/api/notifications", inbox([item(), critical])));
    renderApp("/notifications");
    await screen.findByText(/waiting for you/);

    vi.useFakeTimers();
    try {
      await act(async () => {
        await vi.advanceTimersByTimeAsync(60 * 60 * 1000);
      });
    } finally {
      vi.useRealTimers();
    }

    expect(screen.getAllByRole("listitem")).toHaveLength(2);
    expect(screen.getByRole("button", { name: "Acknowledge" })).toBeInTheDocument();
  });

  it("says so when there is nothing", async () => {
    signedInAs(agent);

    renderApp("/notifications");

    expect(await screen.findByText("You have no notifications.")).toBeInTheDocument();
  });

  it("sends someone who is not signed in to sign-in", async () => {
    server.use(http.get("/api/auth/session", () => HttpResponse.json({ title: "Unauthorized", status: 401 }, { status: 401 })));

    renderApp("/notifications");

    expect(await screen.findByRole("heading", { name: "Sign in" })).toBeInTheDocument();
  });
});

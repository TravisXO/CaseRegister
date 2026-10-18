import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { describe, expect, it, vi } from "vitest";
import type { PendingAction } from "../../api/types";
import { admin, fakeApi, problem, recorded, renderApp, resetRecorded, signedInAs } from "../../test/helpers";
import { server } from "../../test/server";

const hash = "9f2c4e7a1b6d8035c4e1a7f2b9d06e3c58a1f4b7d2e9c06a3f5b8d1e4c7a2f90";

const proposal = (overrides: Partial<PendingAction> = {}): PendingAction => ({
  id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
  type: "ApproveApplication",
  target: { userId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", agencyId: null, email: null, name: "Chanda Banda" },
  description: 'Approve the application of Chanda Banda: join a new agency called "Lusaka Homes", verify their certificate, make them a registered agent (flags acknowledged: DuplicateMembershipNumber)',
  payload: { existingAgencyId: null, newAgencyName: "Lusaka Homes", acknowledgedFlags: ["DuplicateMembershipNumber"] },
  payloadHash: hash,
  reason: "I checked the certificate against the association's list.",
  status: "Pending",
  proposedBy: { userId: "cccccccc-cccc-4ccc-8ccc-cccccccccccc", name: "Carol Zulu" },
  proposedAt: "2026-10-13T08:05:00+00:00",
  expiresAt: "2026-10-16T08:05:00+00:00",
  decidedByUserId: null,
  decidedAt: null,
  decisionReason: null,
  decidedByName: null,
  lastRefusal: null,
  ...overrides,
});

const open = (action: PendingAction) => {
  signedInAs(admin);
  server.use(http.get(`/api/admin/pending-actions/${action.id}`, () => HttpResponse.json(action)));
  renderApp(`/admin/proposals/${action.id}`);
};

describe("confirming a proposal", () => {
  it("shows who, what, why and any flags in plain language, with times in Zambian time", async () => {
    open(proposal());

    expect(await screen.findByText(/Approve the application of Chanda Banda/)).toBeInTheDocument();
    expect(screen.getByText(/^Carol Zulu, 13 Oct 2026, 10:05 \(Zambian time\) \(/)).toBeInTheDocument();
    expect(screen.getByText("I checked the certificate against the association's list.")).toBeInTheDocument();
    expect(screen.getByText('A new agency called "Lusaka Homes" will be created for them.')).toBeInTheDocument();
    expect(screen.getByText("Another account or another waiting application claims the same membership number.")).toBeInTheDocument();
    // 08:05 UTC is 10:05 in Zambia.
    expect(screen.getByText(/16 Oct 2026, 10:05 \(Zambian time\) \(/)).toBeInTheDocument();
    expect(screen.getByText(/Fingerprint of exactly this proposal: 9f2c4e7a1b6d8035…/)).toBeInTheDocument();
  });

  it("sends the hash of the proposal on the screen, the code, and the anti-forgery token", async () => {
    resetRecorded();
    const action = proposal();
    fakeApi({ [`POST /api/admin/pending-actions/${action.id}/confirm`]: () => ({ ...action, status: "Confirmed" }) });
    open(action);
    await screen.findByText(/Approve the application of Chanda Banda/);

    await userEvent.type(screen.getByLabelText("Code from your authenticator app"), "654321");
    await userEvent.click(screen.getByRole("button", { name: "Confirm" }));

    await waitFor(() => { expect(recorded.some((item) => item.path.endsWith("/confirm"))).toBe(true); });
    const call = recorded.find((item) => item.path.endsWith("/confirm"));
    expect(call?.body).toEqual({ payloadHash: hash, twoFactorCode: "654321" });
    expect(call?.csrf).toBe("csrf-1");
  });

  it("sends the new hash, not the old one, after the proposal turns out to have changed", async () => {
    resetRecorded();
    const first = proposal();
    const changed = proposal({ payloadHash: "0123456789abcdef".repeat(4), reason: "A different reason, given after it was changed." });
    let confirms = 0;
    signedInAs(admin);
    server.use(http.get(`/api/admin/pending-actions/${first.id}`, () => HttpResponse.json(confirms === 0 ? first : changed)));
    fakeApi({
      [`POST /api/admin/pending-actions/${first.id}/confirm`]: () => {
        confirms += 1;

        return confirms === 1 ? problem(409, "Not what you approved", "This proposal is not the one you were shown. Read it again before confirming.") : { ...changed, status: "Confirmed" };
      },
    });
    renderApp(`/admin/proposals/${first.id}`);
    await screen.findByText(/Approve the application of Chanda Banda/);
    await userEvent.type(screen.getByLabelText("Code from your authenticator app"), "111111");
    await userEvent.click(screen.getByRole("button", { name: "Confirm" }));

    await waitFor(() => { expect(screen.getByText("A different reason, given after it was changed.")).toBeInTheDocument(); });
    expect(screen.getByRole("alert")).toHaveTextContent("This proposal is not the one you were shown.");
    await userEvent.type(screen.getByLabelText("Code from your authenticator app"), "222222");
    await userEvent.click(screen.getByRole("button", { name: "Confirm" }));

    await waitFor(() => { expect(confirms).toBe(2); });
    const bodies = recorded.filter((item) => item.path.endsWith("/confirm")).map((item) => item.body);
    expect(bodies).toEqual([
      { payloadHash: hash, twoFactorCode: "111111" },
      { payloadHash: "0123456789abcdef".repeat(4), twoFactorCode: "222222" },
    ]);
  });

  it("sends only once when the button is pressed twice", async () => {
    resetRecorded();
    const action = proposal();
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    fakeApi({});
    server.use(
      http.post(`/api/admin/pending-actions/${action.id}/confirm`, async ({ request }) => {
        recorded.push({ method: "POST", path: new URL(request.url).pathname, csrf: null, body: undefined });
        await gate;

        return HttpResponse.json({ ...action, status: "Confirmed" });
      }),
    );
    open(action);
    await screen.findByText(/Approve the application of Chanda Banda/);
    await userEvent.type(screen.getByLabelText("Code from your authenticator app"), "654321");
    const button = screen.getByRole("button", { name: "Confirm" });

    await userEvent.click(button);
    await userEvent.click(button);
    release();

    await waitFor(() => { expect(screen.getByRole("button", { name: "Confirm" })).toBeEnabled(); });
    expect(recorded.filter((item) => item.path.endsWith("/confirm"))).toHaveLength(1);
  });

  it("will not ask for a code to be sent with nothing typed", async () => {
    resetRecorded();
    open(proposal());
    await screen.findByText(/Approve the application of Chanda Banda/);

    await userEvent.click(screen.getByRole("button", { name: "Confirm" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Enter a code from your authenticator app");
    expect(recorded.filter((item) => item.path.endsWith("/confirm"))).toHaveLength(0);
  });

  it("shows a staff invite's setup link once, to copy or download, with a warning before leaving", async () => {
    resetRecorded();
    const invite = proposal({
      type: "InviteStaff",
      target: { userId: null, agencyId: null, email: "dan@example.test", name: "Dan Mumba" },
      description: "Invite Dan Mumba (dan@example.test) as CaseReviewer. They set a password and two-factor from a link that lasts 24 hours",
      payload: { email: "dan@example.test", fullName: "Dan Mumba", role: "CaseReviewer" },
    });
    let confirmed = false;
    signedInAs(admin);
    server.use(http.get(`/api/admin/pending-actions/${invite.id}`, () => HttpResponse.json(confirmed ? { ...invite, status: "Confirmed" } : invite)));
    fakeApi({
      [`POST /api/admin/pending-actions/${invite.id}/confirm`]: () => {
        confirmed = true;

        return { ...invite, status: "Confirmed", setupLink: "https://localhost:5173/staff/setup#one-time-token" };
      },
    });
    renderApp(`/admin/proposals/${invite.id}`);
    expect(await screen.findByText("Dan Mumba, dan@example.test, as Case reviewer")).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("Code from your authenticator app"), "654321");

    await userEvent.click(screen.getByRole("button", { name: "Confirm" }));

    expect(await screen.findByText("https://localhost:5173/staff/setup#one-time-token")).toBeInTheDocument();
    expect(screen.getByText(/This is the only time it is shown/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Copy" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Download as a text file" })).toBeInTheDocument();
    const closing = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(closing);
    expect(closing.defaultPrevented).toBe(true);

    await userEvent.click(screen.getByRole("checkbox"));
    await userEvent.click(screen.getByRole("button", { name: "I have sent it" }));
    expect(screen.queryByText("https://localhost:5173/staff/setup#one-time-token")).not.toBeInTheDocument();
  });

  it("offers an admin their own proposal to withdraw, and never to confirm", async () => {
    resetRecorded();
    const mine = proposal({ proposedBy: { userId: admin.id, name: "Brian Phiri" } });
    fakeApi({ [`POST /api/admin/pending-actions/${mine.id}/withdraw`]: () => ({ ...mine, status: "Withdrawn" }) });
    open(mine);

    expect(await screen.findByText(/a different admin has to confirm it/i)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Confirm" })).not.toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("Reason"), "Proposed it against the wrong person.");
    await userEvent.click(screen.getByRole("button", { name: "Withdraw" }));

    await waitFor(() => { expect(recorded.some((item) => item.path.endsWith("/withdraw"))).toBe(true); });
    expect(recorded.find((item) => item.path.endsWith("/withdraw"))?.body).toEqual({ reason: "Proposed it against the wrong person." });
  });

  it("rejects with a reason, and says no more once it has been decided", async () => {
    resetRecorded();
    const action = proposal();
    let status: PendingAction["status"] = "Pending";
    signedInAs(admin);
    server.use(http.get(`/api/admin/pending-actions/${action.id}`, () => HttpResponse.json({ ...action, status })));
    fakeApi({
      [`POST /api/admin/pending-actions/${action.id}/reject`]: () => {
        status = "Rejected";

        return { ...action, status };
      },
    });
    renderApp(`/admin/proposals/${action.id}`);
    await screen.findByText(/Approve the application of Chanda Banda/);

    await userEvent.type(screen.getByLabelText("Reason"), "The certificate number does not match.");
    await userEvent.click(screen.getByRole("button", { name: "Reject" }));

    expect(await screen.findByText(/This proposal is no longer waiting: it is rejected\./)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Confirm" })).not.toBeInTheDocument();
    expect(recorded.find((item) => item.path.endsWith("/reject"))?.body).toEqual({ reason: "The certificate number does not match." });
  });
});

describe("the reason on an admin action", () => {
  it("shows the limit and a live count, and refuses a reason that is too short without sending it", async () => {
    resetRecorded();
    const action = proposal();
    fakeApi({ [`POST /api/admin/pending-actions/${action.id}/reject`]: () => ({ ...action, status: "Rejected" }) });
    open(action);
    await screen.findByText(/Approve the application of Chanda Banda/);
    const reason = screen.getByLabelText("Reason");

    expect(reason).toHaveAccessibleDescription(/0 of 500 characters \(at least 10\)/);
    expect(screen.getByText(/Say why, in 10 to 500 characters/)).toBeInTheDocument();

    await userEvent.type(reason, "too short");
    expect(reason).toHaveAccessibleDescription(/9 of 500 characters \(at least 10\)/);
    await userEvent.click(screen.getByRole("button", { name: "Reject" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Give a reason of 10 to 500 characters.");
    expect(recorded.filter((item) => item.path.endsWith("/reject"))).toHaveLength(0);

    await userEvent.type(reason, "!");
    expect(reason).toHaveAccessibleDescription(/10 of 500 characters/);
  });

  it("counts the characters of a long reason and refuses one over the limit", async () => {
    const action = proposal();
    open(action);
    await screen.findByText(/Approve the application of Chanda Banda/);
    const reason = screen.getByLabelText("Reason");

    await userEvent.click(reason);
    await userEvent.paste("x".repeat(501));

    expect(reason).toHaveAccessibleDescription(/501 of 500 characters/);
    await userEvent.click(screen.getByRole("button", { name: "Reject" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Give a reason of 10 to 500 characters.");
  });
});

describe("the proposals list", () => {
  it("lists what is waiting, with a way in to each", async () => {
    signedInAs(admin);
    server.use(http.get("/api/admin/pending-actions", () => HttpResponse.json([proposal()])));

    renderApp("/admin/proposals");

    expect(await screen.findByText(/Approve the application of Chanda Banda/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open" })).toHaveAttribute("href", "/admin/proposals/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
  });

  it("says plainly when nothing is waiting", async () => {
    signedInAs(admin);
    server.use(http.get("/api/admin/pending-actions", () => HttpResponse.json([])));

    renderApp("/admin/proposals");

    expect(await screen.findByText("Nothing is waiting.")).toBeInTheDocument();
  });

  it("shows the error, with a way to try again, when it cannot be loaded", async () => {
    signedInAs(admin);
    let calls = 0;
    server.use(http.get("/api/admin/pending-actions", () => {
      calls += 1;

      return calls === 1 ? problem(500, "Something went wrong") : HttpResponse.json([]);
    }));

    renderApp("/admin/proposals");

    expect(await screen.findByRole("alert")).toHaveTextContent("Something went wrong");
    expect(screen.getByRole("alert")).toHaveTextContent("Reference: trace-123");
    await userEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByText("Nothing is waiting.")).toBeInTheDocument();
  });

  it("shows that it is loading", async () => {
    signedInAs(admin);
    server.use(http.get("/api/admin/pending-actions", async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));

      return HttpResponse.json([]);
    }));

    renderApp("/admin/proposals");

    expect(await screen.findByText("Loading proposals…")).toBeInTheDocument();
    expect(await screen.findByText("Nothing is waiting.")).toBeInTheDocument();
  });
});

describe("the page asks the API for no more than it was asked to", () => {
  it("(a proposal page) makes the one call it needs", async () => {
    const action = proposal();
    const calls = vi.fn();
    server.events.on("request:start", ({ request }) => {
      calls(new URL(request.url).pathname);
    });
    open(action);
    await screen.findByText(/Approve the application of Chanda Banda/);

    expect(calls.mock.calls.map((call) => call[0] as string).filter((path) => path.startsWith("/api/admin"))).toEqual([`/api/admin/pending-actions/${action.id}`]);
    server.events.removeAllListeners();
    within(document.body);
  });
});

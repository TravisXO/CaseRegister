import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { describe, expect, it } from "vitest";
import type { AuditPage, PendingAction } from "../../api/types";
import { admin, agent, renderApp, signedInAs } from "../../test/helpers";
import { server } from "../../test/server";

const entry = (overrides: Partial<AuditPage["entries"][number]> = {}): AuditPage["entries"][number] => ({
  sequence: 7,
  occurredAt: new Date(Date.now() - 3 * 3600 * 1000).toISOString(),
  actorType: "user",
  actorUserId: "d4d4d4d4-d4d4-4d4d-8d4d-d4d4d4d4d4d4",
  actorRole: "Admin",
  action: "ActionConfirmed",
  targetType: "User",
  targetId: "b2b2b2b2-b2b2-4b2b-8b2b-b2b2b2b2b2b2",
  agencyId: null,
  correlationId: "c",
  ipAddress: null,
  detail: null,
  before: null,
  after: null,
  actorName: "Brian Phiri",
  targetName: "Chanda Banda",
  ...overrides,
});

const page = (entries: AuditPage["entries"]): AuditPage => ({ entries, page: 1, pageSize: 25, total: entries.length });

describe("the audit log, read by names", () => {
  it("shows names and plain role words, the exact time and how long ago, and keeps the ids behind an expand for admins", async () => {
    signedInAs(admin);
    server.use(http.get("/api/audit/entries", () => HttpResponse.json(page([entry()]))));
    renderApp("/admin/audit");

    expect(await screen.findByText("Brian Phiri (Admin)")).toBeInTheDocument();
    expect(screen.getByText("Chanda Banda")).toBeInTheDocument();
    expect(screen.getByText("3 hours ago")).toBeInTheDocument();
    const row = screen.getByText("Chanda Banda").closest("tr")!;
    const ids = within(row).getByText("IDs");
    expect(ids.closest("details")).not.toHaveAttribute("open");
    await userEvent.click(ids);
    expect(ids.closest("details")).toHaveAttribute("open");
    expect(within(row).getByText(/Who: d4d4d4d4-d4d4-4d4d-8d4d-d4d4d4d4d4d4/)).toBeInTheDocument();
    expect(within(row).getByText(/About: b2b2b2b2-b2b2-4b2b-8b2b-b2b2b2b2b2b2/)).toBeInTheDocument();
  });

  it("gives a compliance officer words only: no ids at all, and a role in place of a name that is not theirs to know", async () => {
    signedInAs({ ...agent, roles: ["RegisteredAgent", "AgencyComplianceOfficer"] });
    server.use(
      http.get("/api/audit/entries", () =>
        HttpResponse.json(page([entry({ actorName: null, actorRole: "Admin", targetName: null, targetType: "Agency", targetId: "a1a1a1a1-a1a1-4a1a-8a1a-a1a1a1a1a1a1" })])),
      ),
    );
    renderApp("/admin/audit");

    expect(await screen.findByText("An admin")).toBeInTheDocument();
    expect(screen.queryByText("IDs")).not.toBeInTheDocument();
    expect(screen.queryByText(/d4d4d4d4/)).not.toBeInTheDocument();
    expect(screen.queryByText(/a1a1a1a1/)).not.toBeInTheDocument();
  });

  it("shows an email address entry as a code, never as an address, whichever form it is in", async () => {
    signedInAs(admin);
    server.use(
      http.get("/api/audit/entries", () =>
        HttpResponse.json(
          page([
            entry({ sequence: 9, action: "SignInFailed", actorType: "system", actorUserId: null, actorRole: "system", actorName: null, targetType: "Email", targetId: "email-hmac:p1:0123456789abcdef0123456789abcdef", targetName: null }),
            entry({ sequence: 8, action: "SignInFailed", actorType: "system", actorUserId: null, actorRole: "system", actorName: null, targetType: "Email", targetId: "email-sha256:aaaaaaaaaaaaaaaa", targetName: null }),
          ]),
        ),
      ),
    );
    renderApp("/admin/audit");

    expect(await screen.findAllByText("An email address (kept only as a code)")).toHaveLength(2);
    expect(screen.getAllByText("The system")).toHaveLength(2);
  });
});

describe("a proposal, read by names", () => {
  const base: PendingAction = {
    id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
    type: "AssignRole",
    target: { userId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", agencyId: null, email: null, name: "Chanda Banda" },
    description: "Give Chanda Banda the Case reviewer role",
    payload: { role: "CaseReviewer" },
    payloadHash: "9f2c4e7a1b6d8035c4e1a7f2b9d06e3c58a1f4b7d2e9c06a3f5b8d1e4c7a2f90",
    reason: "Needed for the new cases team.",
    status: "Confirmed",
    proposedBy: { userId: "cccccccc-cccc-4ccc-8ccc-cccccccccccc", name: "Carol Zulu" },
    proposedAt: new Date(Date.now() - 5 * 3600 * 1000).toISOString(),
    expiresAt: new Date(Date.now() + 2 * 86_400_000).toISOString(),
    decidedByUserId: "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
    decidedAt: new Date(Date.now() - 2 * 3600 * 1000).toISOString(),
    decisionReason: null,
    decidedByName: "Dan Mumba",
    lastRefusal: null,
  };

  const open = (action: PendingAction) => {
    signedInAs(admin);
    server.use(http.get(`/api/admin/pending-actions/${action.id}`, () => HttpResponse.json(action)));
    renderApp(`/admin/proposals/${action.id}`);
  };

  it("says who proposed it and who confirmed it, each with the exact time and how long ago", async () => {
    open(base);

    expect(await screen.findByText(/^Carol Zulu, .* \(Zambian time\) \(5 hours ago\)$/)).toBeInTheDocument();
    expect(screen.getByText("Confirmed by")).toBeInTheDocument();
    expect(screen.getByText(/^Dan Mumba, .* \(Zambian time\) \(2 hours ago\)$/)).toBeInTheDocument();
    expect(screen.getByText(/\(in 2 days\)/)).toBeInTheDocument();
    expect(screen.getByText("Case reviewer")).toBeInTheDocument();
    expect(screen.queryByText("CaseReviewer")).not.toBeInTheDocument();
  });

  it("says rejected by for a rejection", async () => {
    open({ ...base, status: "Rejected", decisionReason: "Not needed." });

    expect(await screen.findByText("Rejected by")).toBeInTheDocument();
  });

  it("keeps the ids behind a Technical details expand", async () => {
    open(base);
    const summary = await screen.findByText("Technical details (IDs)");

    expect(summary.closest("details")).not.toHaveAttribute("open");
    await userEvent.click(summary);
    expect(screen.getByText("dddddddd-dddd-4ddd-8ddd-dddddddddddd")).toBeInTheDocument();
    expect(screen.getByText("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")).toBeInTheDocument();
  });

  it("has no 'by' line for a proposal that is still waiting", async () => {
    open({ ...base, status: "Pending", decidedByUserId: null, decidedAt: null, decidedByName: null });

    await screen.findByText(/Give Chanda Banda/);
    expect(screen.queryByText("Confirmed by")).not.toBeInTheDocument();
    expect(screen.queryByText("Rejected by")).not.toBeInTheDocument();
  });
});

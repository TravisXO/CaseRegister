import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { describe, expect, it } from "vitest";
import type { ApplicationView, AuditPage } from "../../api/types";
import { admin, agent, fakeApi, problem, recorded, renderApp, resetRecorded, signedInAs } from "../../test/helpers";
import { server } from "../../test/server";

const application = (overrides: Partial<ApplicationView> = {}): ApplicationView => ({
  applicationId: "a1a1a1a1-a1a1-4a1a-8a1a-a1a1a1a1a1a1",
  userId: "b2b2b2b2-b2b2-4b2b-8b2b-b2b2b2b2b2b2",
  fullName: "Chanda Banda",
  email: "chanda@example.test",
  membershipNumber: "AGT-2001",
  membershipClass: "Full",
  agencyName: "Kafue Properties Ltd",
  certificateNumber: "9001/2026",
  certificateYear: 2026,
  flags: [],
  submittedAt: "2026-10-13T08:05:00+00:00",
  reapplicationCount: 0,
  suggestedAgencies: [{ id: "c3c3c3c3-c3c3-4c3c-8c3c-c3c3c3c3c3c3", name: "Kafue Properties" }],
  ...overrides,
});

const reason = "I have checked every detail against the register.";

describe("applications", () => {
  const show = (...applications: ApplicationView[]) => {
    signedInAs(admin);
    server.use(http.get("/api/admin/applications", () => HttpResponse.json(applications)));
    renderApp("/admin/applications");
  };

  it("lists each one with what they claimed, and times in Zambian time", async () => {
    show(application());

    expect(await screen.findByRole("heading", { name: "Chanda Banda" })).toBeInTheDocument();
    expect(screen.getByText("AGT-2001 (Full)")).toBeInTheDocument();
    expect(screen.getByText("Kafue Properties Ltd")).toBeInTheDocument();
    expect(screen.getByText("9001/2026 (valid for 2026)")).toBeInTheDocument();
    expect(screen.getByText("13 Oct 2026, 10:05 (Zambian time)")).toBeInTheDocument();
  });

  it("says plainly when none are waiting", async () => {
    show();

    expect(await screen.findByText("No applications are waiting.")).toBeInTheDocument();
  });

  it("proposes approving, into the agency suggested, as a proposal and not as the decision", async () => {
    resetRecorded();
    fakeApi({ "POST /api/admin/pending-actions": () => ({ id: "x" }) });
    show(application());
    await userEvent.click(await screen.findByRole("button", { name: "Approve…" }));

    expect(screen.getByRole("radio", { name: "Existing agency: Kafue Properties" })).toBeChecked();
    await userEvent.type(screen.getByLabelText("Reason"), reason);
    await userEvent.click(screen.getByRole("button", { name: "Propose approval" }));

    expect(await screen.findByText(/A different admin has to confirm it/)).toBeInTheDocument();
    expect(recorded.find((item) => item.path === "/api/admin/pending-actions")?.body).toEqual({
      type: "ApproveApplication",
      targetUserId: "b2b2b2b2-b2b2-4b2b-8b2b-b2b2b2b2b2b2",
      targetAgencyId: null,
      role: null,
      certificateId: null,
      reason,
      existingAgencyId: "c3c3c3c3-c3c3-4c3c-8c3c-c3c3c3c3c3c3",
      acknowledgedFlags: [],
    });
  });

  it("proposes a new agency by name when that is chosen", async () => {
    resetRecorded();
    fakeApi({ "POST /api/admin/pending-actions": () => ({ id: "x" }) });
    show(application({ suggestedAgencies: [], agencyName: "Lusaka Homes" }));
    await userEvent.click(await screen.findByRole("button", { name: "Approve…" }));

    expect(screen.getByLabelText("Name of the new agency")).toHaveValue("Lusaka Homes");
    await userEvent.type(screen.getByLabelText("Reason"), reason);
    await userEvent.click(screen.getByRole("button", { name: "Propose approval" }));

    await waitFor(() => { expect(recorded.some((item) => item.path === "/api/admin/pending-actions")).toBe(true); });
    expect(recorded.find((item) => item.path === "/api/admin/pending-actions")?.body).toMatchObject({ newAgencyName: "Lusaka Homes", acknowledgedFlags: [] });
    expect(recorded.find((item) => item.path === "/api/admin/pending-actions")?.body).not.toHaveProperty("existingAgencyId");
  });

  it("will not propose approving until every flag has been read and ticked, and then sends them", async () => {
    resetRecorded();
    fakeApi({ "POST /api/admin/pending-actions": () => ({ id: "x" }) });
    show(application({ flags: ["DuplicateMembershipNumber", "Reapplication"] }));
    await userEvent.click(await screen.findByRole("button", { name: "Approve…" }));
    await userEvent.type(screen.getByLabelText("Reason"), reason);

    await userEvent.click(screen.getByRole("button", { name: "Propose approval" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Tick every flag to show you have read it.");
    expect(recorded.filter((item) => item.path === "/api/admin/pending-actions")).toHaveLength(0);

    await userEvent.click(screen.getByRole("checkbox", { name: "Another account or another waiting application claims the same membership number." }));
    await userEvent.click(screen.getByRole("checkbox", { name: "This person was rejected before and has applied again." }));
    await userEvent.click(screen.getByRole("button", { name: "Propose approval" }));

    await waitFor(() => { expect(recorded.some((item) => item.path === "/api/admin/pending-actions")).toBe(true); });
    expect(recorded.find((item) => item.path === "/api/admin/pending-actions")?.body).toMatchObject({ acknowledgedFlags: ["DuplicateMembershipNumber", "Reapplication"] });
  });

  it("proposes rejecting with a reason", async () => {
    resetRecorded();
    fakeApi({ "POST /api/admin/pending-actions": () => ({ id: "x" }) });
    show(application());
    await userEvent.click(await screen.findByRole("button", { name: "Reject…" }));

    await userEvent.type(screen.getByLabelText("Reason"), "The certificate number is not on the list.");
    await userEvent.click(screen.getByRole("button", { name: "Propose rejection" }));

    await waitFor(() => { expect(recorded.some((item) => item.path === "/api/admin/pending-actions")).toBe(true); });
    expect(recorded.find((item) => item.path === "/api/admin/pending-actions")?.body).toMatchObject({ type: "RejectApplication", targetUserId: "b2b2b2b2-b2b2-4b2b-8b2b-b2b2b2b2b2b2", reason: "The certificate number is not on the list." });
  });

  it("shows what the API said when a proposal is refused", async () => {
    fakeApi({ "POST /api/admin/pending-actions": () => problem(409, "Already proposed", "There is already a pending proposal of this kind for this target.") });
    show(application());
    await userEvent.click(await screen.findByRole("button", { name: "Approve…" }));
    await userEvent.type(screen.getByLabelText("Reason"), reason);

    await userEvent.click(screen.getByRole("button", { name: "Propose approval" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("There is already a pending proposal of this kind for this target.");
  });
});

describe("inviting staff", () => {
  it("proposes the invitation with the name, email, role and reason", async () => {
    resetRecorded();
    signedInAs(admin);
    fakeApi({ "POST /api/admin/pending-actions": () => ({ id: "x" }) });
    renderApp("/admin/invite");

    await userEvent.type(await screen.findByLabelText("Full name"), "Dan Mumba");
    await userEvent.type(screen.getByLabelText("Email address"), "dan@example.test");
    await userEvent.selectOptions(screen.getByLabelText("Role"), "Admin");
    await userEvent.type(screen.getByLabelText("Reason"), "A second admin is needed for approvals.");
    await userEvent.click(screen.getByRole("button", { name: "Propose invitation" }));

    expect(await screen.findByText(/The invitation has been proposed/)).toBeInTheDocument();
    expect(recorded.find((item) => item.path === "/api/admin/pending-actions")?.body).toEqual({
      type: "InviteStaff",
      targetUserId: null,
      targetAgencyId: null,
      role: "Admin",
      certificateId: null,
      reason: "A second admin is needed for approvals.",
      inviteEmail: "dan@example.test",
      inviteFullName: "Dan Mumba",
    });
  });

  it("asks for what is missing before sending anything", async () => {
    resetRecorded();
    signedInAs(admin);
    renderApp("/admin/invite");
    await screen.findByLabelText("Full name");

    await userEvent.click(screen.getByRole("button", { name: "Propose invitation" }));

    const summary = await screen.findByRole("alert");
    expect(summary).toHaveTextContent("Enter their full name.");
    expect(summary).toHaveTextContent("Enter their email address.");
    expect(summary).toHaveTextContent("Give a reason of 10 to 500 characters.");
    await waitFor(() => { expect(screen.getByLabelText("Full name")).toHaveFocus(); });
  });
});

describe("immediate account actions", () => {
  const account = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";

  async function fillSuspend() {
    await userEvent.type(await screen.findByLabelText("Account ID", { selector: "#suspend-account" }), account);
    await userEvent.type(document.getElementById("suspend-reason")!, "Left the agency; closing the account.");
  }

  it("asks 'are you sure?' first, says what will happen, and sends nothing until the answer is yes", async () => {
    resetRecorded();
    signedInAs(admin);
    fakeApi({ [`POST /api/admin/users/${account}/suspend`]: () => null });
    renderApp("/admin/actions");
    await fillSuspend();

    await userEvent.click(screen.getByRole("button", { name: "Suspend…" }));

    const dialog = await screen.findByRole("alertdialog");
    expect(dialog).toHaveTextContent(`This suspends the account ${account} and ends all its sessions now.`);
    expect(recorded.filter((item) => item.path.endsWith("/suspend"))).toHaveLength(0);

    await userEvent.click(within(dialog).getByRole("button", { name: "Yes, suspend" }));
    expect(await screen.findByText("Done. It took effect at once and is in the audit log.")).toBeInTheDocument();
    expect(recorded.find((item) => item.path.endsWith("/suspend"))?.body).toEqual({ reason: "Left the agency; closing the account." });
    expect(recorded.find((item) => item.path.endsWith("/suspend"))?.csrf).toBe("csrf-1");
  });

  it("does nothing when the answer is no", async () => {
    resetRecorded();
    signedInAs(admin);
    fakeApi({ [`POST /api/admin/users/${account}/suspend`]: () => null });
    renderApp("/admin/actions");
    await fillSuspend();
    await userEvent.click(screen.getByRole("button", { name: "Suspend…" }));

    await userEvent.click(await screen.findByRole("button", { name: "No, go back" }));

    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(recorded.filter((item) => item.path.endsWith("/suspend"))).toHaveLength(0);
  });

  it("also asks before ending a person's sessions", async () => {
    resetRecorded();
    signedInAs(admin);
    fakeApi({ [`POST /api/admin/users/${account}/end-sessions`]: () => null });
    renderApp("/admin/actions");
    await userEvent.type(await screen.findByLabelText("Account ID", { selector: "#end-sessions-account" }), account);
    await userEvent.type(document.getElementById("end-sessions-reason")!, "Lost phone; signing them out.");

    await userEvent.click(screen.getByRole("button", { name: "End sessions…" }));
    expect(await screen.findByRole("alertdialog")).toHaveTextContent("This signs the account");
    expect(recorded.filter((item) => item.path.endsWith("/end-sessions"))).toHaveLength(0);
    await userEvent.click(screen.getByRole("button", { name: "Yes, end sessions" }));

    expect(await screen.findByText("Done. It took effect at once and is in the audit log.")).toBeInTheDocument();
  });

  it("will not ask about an account ID that is not one, or a reason that is too short", async () => {
    signedInAs(admin);
    renderApp("/admin/actions");
    await userEvent.type(await screen.findByLabelText("Account ID", { selector: "#suspend-account" }), "chanda");
    await userEvent.type(document.getElementById("suspend-reason")!, "short");

    await userEvent.click(screen.getByRole("button", { name: "Suspend…" }));

    const summary = await screen.findByRole("alert");
    expect(summary).toHaveTextContent("Enter the account ID");
    expect(summary).toHaveTextContent("Give a reason of 10 to 500 characters.");
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  });

  it("shows the API's refusal, for instance when the account is an admin", async () => {
    signedInAs(admin);
    fakeApi({ [`POST /api/admin/users/${account}/suspend`]: () => problem(403, "Needs a second admin", "The target is an admin, so this has to be proposed and confirmed by a different admin.") });
    renderApp("/admin/actions");
    await fillSuspend();
    await userEvent.click(screen.getByRole("button", { name: "Suspend…" }));

    await userEvent.click(await screen.findByRole("button", { name: "Yes, suspend" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("The target is an admin, so this has to be proposed and confirmed by a different admin.");
  });

  it("sends only once when 'yes' is pressed twice", async () => {
    resetRecorded();
    signedInAs(admin);
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    fakeApi({});
    server.use(
      http.post(`/api/admin/users/${account}/suspend`, async ({ request }) => {
        recorded.push({ method: "POST", path: new URL(request.url).pathname, csrf: null, body: undefined });
        await gate;

        return new HttpResponse(null, { status: 204 });
      }),
    );
    renderApp("/admin/actions");
    await fillSuspend();
    await userEvent.click(screen.getByRole("button", { name: "Suspend…" }));
    const yes = await screen.findByRole("button", { name: "Yes, suspend" });

    await userEvent.click(yes);
    await userEvent.click(yes);
    release();

    await screen.findByText("Done. It took effect at once and is in the audit log.");
    expect(recorded.filter((item) => item.path.endsWith("/suspend"))).toHaveLength(1);
  });
});

describe("the audit log", () => {
  const page = (overrides: Partial<AuditPage> = {}): AuditPage => ({
    entries: [
      {
        sequence: 42,
        occurredAt: "2026-10-13T08:05:00+00:00",
        actorType: "user",
        actorUserId: "d4d4d4d4-d4d4-4d4d-8d4d-d4d4d4d4d4d4",
        actorRole: "Admin",
        action: "ActionConfirmed",
        targetType: "User",
        targetId: "b2b2b2b2-b2b2-4b2b-8b2b-b2b2b2b2b2b2",
        agencyId: null,
        correlationId: "corr-1",
        ipAddress: "203.0.113.9",
        detail: "approved a proposal",
        before: { state: "Pending" },
        after: { state: "Approved" },
        actorName: "Brian Phiri",
        targetName: "Chanda Banda",
      },
    ],
    page: 1,
    pageSize: 25,
    total: 1,
    ...overrides,
  });

  it("shows the entries newest first, in Zambian time, with the address for an admin", async () => {
    signedInAs(admin);
    server.use(http.get("/api/audit/entries", () => HttpResponse.json(page())));

    renderApp("/admin/audit");

    expect(await screen.findByText("ActionConfirmed")).toBeInTheDocument();
    expect(screen.getByText("13 Oct 2026, 10:05 (Zambian time)")).toBeInTheDocument();
    expect(screen.getByRole("columnheader", { name: "Address" })).toBeInTheDocument();
    expect(screen.getByText("203.0.113.9")).toBeInTheDocument();
    expect(screen.getByText("1 entries, newest first. Page 1 of 1.")).toBeInTheDocument();
  });

  it("offers an admin the 'verify chain' button and shows an intact result", async () => {
    resetRecorded();
    signedInAs(admin);
    server.use(http.get("/api/audit/entries", () => HttpResponse.json(page())));
    fakeApi({ "POST /api/audit/verify": () => ({ intact: true, entriesChecked: 42, headSequence: 42, anchorsChecked: 3, anchorStoreConfigured: true, fromAnchorSequence: null, firstBreak: null, verifiedAt: "2026-10-13T08:10:00+00:00" }) });
    renderApp("/admin/audit");

    await userEvent.click(await screen.findByRole("button", { name: "Verify chain" }));

    expect(await screen.findByText(/The chain is intact: 42 entries checked up to number 42, 3 anchors looked at\./)).toBeInTheDocument();
    expect(recorded.find((item) => item.path === "/api/audit/verify")?.body).toEqual({ fromLastGoodAnchor: false });
    expect(recorded.find((item) => item.path === "/api/audit/verify")?.csrf).toBe("csrf-1");
  });

  it("can start from the last good anchor", async () => {
    resetRecorded();
    signedInAs(admin);
    server.use(http.get("/api/audit/entries", () => HttpResponse.json(page())));
    fakeApi({ "POST /api/audit/verify": () => ({ intact: true, entriesChecked: 5, headSequence: 42, anchorsChecked: 1, anchorStoreConfigured: true, fromAnchorSequence: 37, firstBreak: null, verifiedAt: "2026-10-13T08:10:00+00:00" }) });
    renderApp("/admin/audit");
    await userEvent.click(await screen.findByRole("checkbox", { name: "Start from the last good anchor (quicker)" }));

    await userEvent.click(screen.getByRole("button", { name: "Verify chain" }));

    await waitFor(() => { expect(recorded.some((item) => item.path === "/api/audit/verify")).toBe(true); });
    expect(recorded.find((item) => item.path === "/api/audit/verify")?.body).toEqual({ fromLastGoodAnchor: true });
  });

  it("reports the first broken link when the chain is not intact", async () => {
    signedInAs(admin);
    server.use(http.get("/api/audit/entries", () => HttpResponse.json(page())));
    fakeApi({ "POST /api/audit/verify": () => ({ intact: false, entriesChecked: 10, headSequence: 42, anchorsChecked: 1, anchorStoreConfigured: true, fromAnchorSequence: null, firstBreak: { kind: "BadHash", sequence: 11, message: "Entry 11 does not match its hash." }, verifiedAt: "2026-10-13T08:10:00+00:00" }) });
    renderApp("/admin/audit");

    await userEvent.click(await screen.findByRole("button", { name: "Verify chain" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("The chain is broken at entry 11: BadHash. Entry 11 does not match its hash.");
  });

  it("does not offer a compliance officer the verify button, and shows no addresses", async () => {
    signedInAs({ ...agent, roles: ["AgencyComplianceOfficer", "RegisteredAgent"] });
    server.use(
      http.get("/api/me/practising-certificates", () => HttpResponse.json([])),
      http.get("/api/audit/entries", () => HttpResponse.json(page({ entries: [{ ...(page().entries[0]!), ipAddress: null }] }))),
    );

    renderApp("/admin/audit");

    expect(await screen.findByText("ActionConfirmed")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Verify chain" })).not.toBeInTheDocument();
    expect(screen.queryByRole("columnheader", { name: "Address" })).not.toBeInTheDocument();
  });

  it("filters by date in Zambian time, by actor and by action, and pages through the results", async () => {
    signedInAs(admin);
    const queries: string[] = [];
    server.use(
      http.get("/api/audit/entries", ({ request }) => {
        queries.push(new URL(request.url).search);

        return HttpResponse.json(page({ total: 60 }));
      }),
    );
    renderApp("/admin/audit");
    await screen.findByText("ActionConfirmed");

    await userEvent.type(screen.getByLabelText("From (date)"), "2026-10-01");
    await userEvent.type(screen.getByLabelText("To (date)"), "2026-10-13");
    await userEvent.type(screen.getByLabelText("What was done"), "SignedIn");
    await userEvent.click(screen.getByRole("button", { name: "Show" }));

    await waitFor(() => { expect(queries.some((query) => query.includes("action=SignedIn"))).toBe(true); });
    const filtered = new URLSearchParams(queries.find((query) => query.includes("action=SignedIn")));
    expect(filtered.get("from")).toBe("2026-10-01T00:00:00+02:00");
    expect(filtered.get("to")).toBe("2026-10-13T23:59:59.999+02:00");
    expect(filtered.get("page")).toBe("1");
    expect(filtered.get("pageSize")).toBe("25");

    await userEvent.click(await screen.findByRole("button", { name: "Older" }));
    await waitFor(() => { expect(queries.some((query) => query.includes("page=2"))).toBe(true); });
  });

  it("says plainly when nothing matches", async () => {
    signedInAs(admin);
    server.use(http.get("/api/audit/entries", () => HttpResponse.json(page({ entries: [], total: 0 }))));

    renderApp("/admin/audit");

    expect(await screen.findByText("No entries match.")).toBeInTheDocument();
  });
});

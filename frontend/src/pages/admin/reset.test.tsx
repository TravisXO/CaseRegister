import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { describe, expect, it } from "vitest";
import type { PendingAction } from "../../api/types";
import { admin, agent, fakeApi, problem, recorded, renderApp, resetRecorded, signedInAs } from "../../test/helpers";
import { server } from "../../test/server";

const hash = "9f2c4e7a1b6d8035c4e1a7f2b9d06e3c58a1f4b7d2e9c06a3f5b8d1e4c7a2f90";
const person = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

const reset = (overrides: Partial<PendingAction> = {}): PendingAction => ({
  id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
  type: "ResetTwoFactor",
  target: { userId: person, agencyId: null, email: null, name: "Chanda Banda" },
  description: "Reset two-factor for Chanda Banda: remove the authenticator and recovery codes, end all sessions, and make a one-time code they need to set it up again",
  payload: {},
  payloadHash: hash,
  reason: "Saw their ID card in person and phoned the number on file.",
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

describe("the one-time enrolment code after a reset", () => {
  it("is shown once to the confirming admin, with a warning about handing it over, and not again", async () => {
    resetRecorded();
    const action = reset();
    signedInAs(admin);
    let confirmed = false;
    server.use(http.get(`/api/admin/pending-actions/${action.id}`, () => HttpResponse.json(confirmed ? { ...action, status: "Confirmed" } : action)));
    fakeApi({
      [`POST /api/admin/pending-actions/${action.id}/confirm`]: () => {
        confirmed = true;

        return { ...action, status: "Confirmed", enrolmentCode: "AB3D-EF4G-HJ5K" };
      },
    });
    renderApp(`/admin/proposals/${action.id}`);
    await screen.findByText(/Reset two-factor for Chanda Banda/);

    await userEvent.type(screen.getByLabelText("Code from your authenticator app"), "654321");
    await userEvent.click(screen.getByRole("button", { name: "Confirm" }));

    expect(await screen.findByText("AB3D-EF4G-HJ5K")).toBeInTheDocument();
    expect(screen.getByText(/in person or by phone, never in writing/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole("checkbox"));
    await userEvent.click(screen.getByRole("button", { name: "I have given it to them" }));
    await waitFor(() => { expect(screen.queryByText("AB3D-EF4G-HJ5K")).not.toBeInTheDocument(); });
  });
});

describe("signing in after a reset", () => {
  it("asks for the enrolment code after the password, and goes on to set two-factor up", async () => {
    resetRecorded();
    let signedIn = false;
    server.use(
      http.get("/api/auth/session", () => (signedIn ? HttpResponse.json({ userId: agent.id, twoFactorComplete: false }) : problem(401, "Unauthorized"))),
    );
    fakeApi({
      "POST /api/auth/sign-in": () => ({ status: "enrolment-code-required" }),
      "POST /api/auth/2fa/enrolment-code": () => {
        signedIn = true;

        return { status: "two-factor-enrolment-required" };
      },
      "POST /api/auth/2fa/enrol": () => ({ otpAuthUri: "otpauth://totp/CaseRegister:ada?secret=JBSWY3DPEHPK3PXP", sharedKey: "JBSWY3DPEHPK3PXP" }),
    });
    renderApp("/sign-in");
    await userEvent.type(await screen.findByLabelText("Email address"), "ada@example.test");
    await userEvent.type(screen.getByLabelText("Password"), "correct horse battery staple");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByRole("heading", { name: "Enrolment code" })).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("Enrolment code"), "AB3D-EF4G-HJ5K");
    await userEvent.click(screen.getByRole("button", { name: "Continue" }));

    await waitFor(() => { expect(recorded.some((item) => item.path === "/api/auth/2fa/enrolment-code")).toBe(true); });
    expect(recorded.find((item) => item.path === "/api/auth/2fa/enrolment-code")?.body).toEqual({ code: "AB3D-EF4G-HJ5K" });
    expect(await screen.findByRole("heading", { name: /two-factor/i })).toBeInTheDocument();
  });

  it("says plainly when the code is not accepted, and clears the box", async () => {
    resetRecorded();
    server.use(http.get("/api/auth/session", () => problem(401, "Unauthorized")));
    fakeApi({ "POST /api/auth/2fa/enrolment-code": () => problem(401, "Verification failed", "That code was not accepted. Check it, or ask an admin for a new one.") });
    renderApp("/sign-in/enrolment-code");

    await userEvent.type(await screen.findByLabelText("Enrolment code"), "AAAA-BBBB-CCCC");
    await userEvent.click(screen.getByRole("button", { name: "Continue" }));

    expect(await screen.findByText(/That code was not accepted/)).toBeInTheDocument();
    expect(screen.getByLabelText("Enrolment code")).toHaveValue("");
  });
});

describe("proposing a reset", () => {
  it("needs the account and a reason that says how the identity was checked", async () => {
    resetRecorded();
    signedInAs(admin);
    fakeApi({ "POST /api/admin/pending-actions": () => reset() });
    renderApp("/admin/actions");
    await screen.findByRole("heading", { name: "Reset someone's two-factor" });

    await userEvent.type(screen.getByLabelText("Account ID", { selector: "#reset-account" }), person);
    await userEvent.type(document.getElementById("reset-reason")!, "Phoned them, fine.");
    await userEvent.click(screen.getByRole("button", { name: "Propose the reset" }));
    expect((await screen.findAllByText(/Say how you checked who they are/)).length).toBeGreaterThan(0);
    expect(recorded.some((item) => item.path === "/api/admin/pending-actions")).toBe(false);

    await userEvent.clear(document.getElementById("reset-reason")!);
    await userEvent.type(document.getElementById("reset-reason")!, "Saw their ID card in person and phoned the number on file.");
    await userEvent.click(screen.getByRole("button", { name: "Propose the reset" }));

    expect(await screen.findByRole("link", { name: "Open the proposal" })).toBeInTheDocument();
    const body = recorded.find((item) => item.path === "/api/admin/pending-actions")?.body as Record<string, unknown>;
    expect(body).toMatchObject({ type: "ResetTwoFactor", targetUserId: person });
  });
});

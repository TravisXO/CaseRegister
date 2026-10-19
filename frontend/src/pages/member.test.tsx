import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { describe, expect, it } from "vitest";
import type { CertificateInfo, SessionInfo } from "../api/types";
import { admin, agent, fakeApi, problem, recorded, renderApp, resetRecorded, signedInAs } from "../test/helpers";
import { server } from "../test/server";

const certificate = (overrides: Partial<CertificateInfo> = {}): CertificateInfo => ({
  id: "e5e5e5e5-e5e5-4e5e-8e5e-e5e5e5e5e5e5",
  number: "9611/2026",
  validFrom: "2026-01-01",
  validTo: "2026-12-31",
  isVerified: true,
  verifiedAt: "2026-10-01T08:00:00+00:00",
  isCurrent: true,
  ...overrides,
});

const certificates = (...items: CertificateInfo[]) => server.use(http.get("/api/me/practising-certificates", () => HttpResponse.json(items)));

describe("my profile", () => {
  it("shows who I am, my agency, my roles and my certificates, with what each one's status means", async () => {
    signedInAs(agent);
    certificates(certificate(), certificate({ id: "f6f6f6f6-f6f6-4f6f-8f6f-f6f6f6f6f6f6", number: "9000/2027", validFrom: "2027-01-01", validTo: "2027-12-31", isVerified: false, verifiedAt: null, isCurrent: false }));

    renderApp("/profile");

    expect(await screen.findByText("Ada Mwansa")).toBeInTheDocument();
    expect(screen.getByText("ada@example.test")).toBeInTheDocument();
    expect(screen.getByText("Kafue Properties")).toBeInTheDocument();
    expect(screen.getByText("Registered agent")).toBeInTheDocument();
    expect(await screen.findByText("9611/2026")).toBeInTheDocument();
    expect(screen.getByText("1 Jan 2026 to 31 Dec 2026")).toBeInTheDocument();
    expect(screen.getByText("Current and verified")).toBeInTheDocument();
    expect(screen.getByText("Waiting for an admin to verify it")).toBeInTheDocument();
    expect(screen.getByText(/Verified 01 Oct 2026, 10:00 \(Zambian time\)|Verified 1 Oct 2026, 10:00 \(Zambian time\)/)).toBeInTheDocument();
  });

  it("says when there are no certificates yet, and shows no certificate section to staff", async () => {
    signedInAs(agent);
    certificates();
    const first = renderApp("/profile");
    expect(await screen.findByText("You have not added a certificate yet.")).toBeInTheDocument();
    first.unmount();

    signedInAs(admin);
    renderApp("/profile");
    expect(await screen.findByText("Brian Phiri")).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Practising certificates" })).not.toBeInTheDocument();
  });

  it("adds a certificate for a calendar year, as the first day to the last, and says it needs verifying", async () => {
    resetRecorded();
    signedInAs(agent);
    certificates();
    fakeApi({ "POST /api/me/practising-certificates": () => ({ id: "e5e5e5e5-e5e5-4e5e-8e5e-e5e5e5e5e5e5" }) });
    renderApp("/profile");
    await screen.findByText("You have not added a certificate yet.");

    await userEvent.type(screen.getByLabelText("Certificate number"), "9611/2027");
    await userEvent.clear(screen.getByLabelText("Year it is valid for"));
    await userEvent.type(screen.getByLabelText("Year it is valid for"), "2027");
    await userEvent.click(screen.getByRole("button", { name: "Add certificate" }));

    expect(await screen.findByText("Added. It counts once an admin has verified it.")).toBeInTheDocument();
    expect(recorded.find((item) => item.path === "/api/me/practising-certificates")?.body).toEqual({ certificateNumber: "9611/2027", validFrom: "2027-01-01", validTo: "2027-12-31" });
  });

  it("shows what the API said when the certificate is refused", async () => {
    signedInAs(agent);
    certificates();
    fakeApi({ "POST /api/me/practising-certificates": () => problem(409, "Already recorded", "A certificate with that number has already been recorded.") });
    renderApp("/profile");
    await screen.findByText("You have not added a certificate yet.");
    await userEvent.type(screen.getByLabelText("Certificate number"), "9611/2026");

    await userEvent.click(screen.getByRole("button", { name: "Add certificate" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("A certificate with that number has already been recorded.");
  });

  it("changes the password with the current one and a code, and clears the form", async () => {
    resetRecorded();
    signedInAs(agent);
    certificates();
    fakeApi({ "POST /api/auth/change-password": () => null });
    renderApp("/profile");
    await screen.findByText("Ada Mwansa");

    await userEvent.type(screen.getByLabelText("Current password"), "the old passphrase");
    await userEvent.type(screen.getByLabelText("New password"), "a brand new passphrase");
    await userEvent.type(screen.getByLabelText("Code from your authenticator app"), "123456");
    await userEvent.click(screen.getByRole("button", { name: "Change password" }));

    expect(await screen.findByText("Your password has been changed.")).toBeInTheDocument();
    expect(recorded.find((item) => item.path === "/api/auth/change-password")?.body).toEqual({ currentPassword: "the old passphrase", newPassword: "a brand new passphrase", twoFactorCode: "123456" });
    expect(screen.getByLabelText("Current password")).toHaveValue("");
    expect(screen.getByLabelText("New password")).toHaveValue("");
  });

  it("uses the right autocomplete values on the password form", async () => {
    signedInAs(agent);
    certificates();
    renderApp("/profile");
    await screen.findByText("Ada Mwansa");

    expect(screen.getByLabelText("Current password")).toHaveAttribute("autocomplete", "current-password");
    expect(screen.getByLabelText("New password")).toHaveAttribute("autocomplete", "new-password");
    expect(screen.getByLabelText("Code from your authenticator app")).toHaveAttribute("autocomplete", "one-time-code");
  });

  it("shows the API's reasons when the new password is not accepted, and does not claim success", async () => {
    signedInAs(agent);
    certificates();
    fakeApi({ "POST /api/auth/change-password": () => problem(400, "The password was not changed", "That password has appeared in a data breach. Choose another.") });
    renderApp("/profile");
    await screen.findByText("Ada Mwansa");
    await userEvent.type(screen.getByLabelText("Current password"), "the old passphrase");
    await userEvent.type(screen.getByLabelText("New password"), "password1234");
    await userEvent.type(screen.getByLabelText("Code from your authenticator app"), "123456");

    await userEvent.click(screen.getByRole("button", { name: "Change password" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("That password has appeared in a data breach. Choose another.");
    expect(screen.queryByText("Your password has been changed.")).not.toBeInTheDocument();
  });
});

describe("my sessions", () => {
  const session = (overrides: Partial<SessionInfo> = {}): SessionInfo => ({
    id: "a7a7a7a7-a7a7-4a7a-8a7a-a7a7a7a7a7a7",
    signedInAt: "2026-10-13T08:05:00+00:00",
    lastActiveAt: "2026-10-13T09:05:00+00:00",
    ipAddress: "203.0.113.9",
    userAgent: "Firefox on Windows",
    isCurrent: false,
    ...overrides,
  });

  it("lists them in Zambian time and offers to end every one but this one", async () => {
    signedInAs(agent);
    certificates();
    server.use(http.get("/api/auth/sessions", () => HttpResponse.json([session({ id: "11111111-0000-4000-8000-000000000001", isCurrent: true }), session()])));

    renderApp("/sessions");

    expect(await screen.findByText("This session")).toBeInTheDocument();
    expect(screen.getByText("Another session")).toBeInTheDocument();
    expect(screen.getAllByText("Signed in 13 Oct 2026, 10:05 (Zambian time)")).toHaveLength(2);
    expect(screen.getAllByRole("button", { name: "End this session" })).toHaveLength(1);
  });

  it("ends one, once, and shows the list again", async () => {
    resetRecorded();
    signedInAs(agent);
    certificates();
    let ended = false;
    server.use(http.get("/api/auth/sessions", () => HttpResponse.json(ended ? [] : [session()])));
    fakeApi({
      "DELETE /api/auth/sessions/a7a7a7a7-a7a7-4a7a-8a7a-a7a7a7a7a7a7": () => {
        ended = true;

        return null;
      },
    });
    renderApp("/sessions");
    const button = await screen.findByRole("button", { name: "End this session" });

    await userEvent.click(button);
    await userEvent.click(button);

    expect(await screen.findByText("No sessions.")).toBeInTheDocument();
    expect(recorded.filter((item) => item.method === "DELETE")).toHaveLength(1);
    expect(recorded.find((item) => item.method === "DELETE")?.csrf).toBe("csrf-1");
  });

  it("shows the error when one cannot be ended", async () => {
    signedInAs(agent);
    certificates();
    server.use(http.get("/api/auth/sessions", () => HttpResponse.json([session()])));
    fakeApi({ "DELETE /api/auth/sessions/a7a7a7a7-a7a7-4a7a-8a7a-a7a7a7a7a7a7": () => problem(404, "Not found", "There is no such session.") });
    renderApp("/sessions");

    await userEvent.click(await screen.findByRole("button", { name: "End this session" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("There is no such session.");
    await waitFor(() => { expect(screen.getByRole("button", { name: "End this session" })).toBeEnabled(); });
  });
});

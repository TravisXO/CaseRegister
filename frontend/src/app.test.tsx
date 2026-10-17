import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { describe, expect, it, vi } from "vitest";
import { ErrorBoundary } from "./components/States";
import { admin, agent, fakeApi, problem, recorded, renderApp, resetRecorded, signedInAs, signedOut } from "./test/helpers";
import { server } from "./test/server";
import type { Profile } from "./api/types";

const certificates = () => server.use(http.get("/api/me/practising-certificates", () => HttpResponse.json([])));

describe("who is signed in", () => {
  it("sends someone who is not signed in to the sign-in page", async () => {
    signedOut();

    renderApp("/profile");

    expect(await screen.findByRole("heading", { name: "Sign in" })).toBeInTheDocument();
  });

  it("shows the profile of someone who is", async () => {
    signedInAs(agent);
    certificates();

    renderApp("/profile");

    expect(await screen.findByText("Ada Mwansa")).toBeInTheDocument();
    expect(screen.getByText("AGT-0101 (Full)")).toBeInTheDocument();
  });

  it("forces someone with no two-factor yet to set it up, whatever they asked for", async () => {
    server.use(http.get("/api/auth/session", () => HttpResponse.json({ userId: agent.id, twoFactorComplete: false })));

    renderApp("/sessions");

    expect(await screen.findByRole("heading", { name: "Set up two-factor sign-in" })).toBeInTheDocument();
  });

  it("goes to the profile from the start page when signed in, and to sign-in when not", async () => {
    signedInAs(agent);
    certificates();
    const first = renderApp("/");
    expect(await screen.findByRole("heading", { name: "My profile" })).toBeInTheDocument();
    first.unmount();

    signedOut();
    renderApp("/");
    expect(await screen.findByRole("heading", { name: "Sign in" })).toBeInTheDocument();
  });
});

describe("signing out", () => {
  it("clears everything about the person from the page and says so", async () => {
    resetRecorded();
    signedInAs(agent);
    certificates();
    fakeApi({ "POST /api/auth/sign-out": () => null });
    renderApp("/profile");
    await screen.findByText("Ada Mwansa");

    await userEvent.click(screen.getByRole("button", { name: "Sign out" }));

    expect(await screen.findByRole("heading", { name: "Sign in" })).toBeInTheDocument();
    expect(screen.getByText("You have signed out.")).toBeInTheDocument();
    expect(screen.queryByText("Ada Mwansa")).not.toBeInTheDocument();
    expect(screen.queryByText("ada@example.test")).not.toBeInTheDocument();
    expect(screen.queryByRole("navigation", { name: "Main" })).not.toBeInTheDocument();
    expect(recorded.find((call) => call.path === "/api/auth/sign-out")?.csrf).toBe("csrf-1");
  });

  it("clears everything even when the server cannot be told", async () => {
    signedInAs(agent);
    certificates();
    fakeApi({ "POST /api/auth/sign-out": () => problem(500, "Server error") });
    renderApp("/profile");
    await screen.findByText("Ada Mwansa");

    await userEvent.click(screen.getByRole("button", { name: "Sign out" }));

    expect(await screen.findByRole("heading", { name: "Sign in" })).toBeInTheDocument();
    expect(screen.queryByText("Ada Mwansa")).not.toBeInTheDocument();
  });

  it("does not offer the way back to a page that was open: going back shows sign-in again", async () => {
    signedInAs(agent);
    certificates();
    fakeApi({ "POST /api/auth/sign-out": () => null });
    const { router } = renderApp("/profile");
    await screen.findByText("Ada Mwansa");
    await userEvent.click(screen.getByRole("button", { name: "Sign out" }));
    await screen.findByRole("heading", { name: "Sign in" });

    await act(async () => {
      await router.navigate("/profile");
    });

    await waitFor(() => { expect(router.state.location.pathname).toBe("/sign-in"); });
    expect(screen.getByRole("heading", { name: "Sign in" })).toBeInTheDocument();
    expect(screen.queryByText("Ada Mwansa")).not.toBeInTheDocument();
  });
});

describe("a 401 in the middle of using the app", () => {
  it("drops everything held in memory and goes back to sign-in with a message", async () => {
    signedInAs(agent);
    server.use(http.get("/api/me/practising-certificates", () => problem(401, "Unauthorized")));

    renderApp("/profile");

    expect(await screen.findByRole("heading", { name: "Sign in" })).toBeInTheDocument();
    expect(screen.getByText("Your session ended. Sign in again to carry on.")).toBeInTheDocument();
    expect(screen.queryByText("Ada Mwansa")).not.toBeInTheDocument();
    expect(screen.queryByText("ada@example.test")).not.toBeInTheDocument();
  });

  it("does the same on an admin page", async () => {
    signedInAs(admin);
    server.use(http.get("/api/admin/applications", () => problem(401, "Unauthorized")));

    renderApp("/admin/applications");

    expect(await screen.findByText("Your session ended. Sign in again to carry on.")).toBeInTheDocument();
    expect(screen.queryByText("Brian Phiri")).not.toBeInTheDocument();
  });
});

describe("403", () => {
  it("shows a plain page, without asking the API, to someone whose role does not allow it", async () => {
    resetRecorded();
    signedInAs(agent);
    certificates();
    fakeApi({});
    let asked = false;
    server.use(http.get("/api/admin/applications", () => {
      asked = true;

      return HttpResponse.json([]);
    }));

    renderApp("/admin/applications");

    expect(await screen.findByRole("heading", { name: "You do not have access to this" })).toBeInTheDocument();
    expect(asked).toBe(false);
  });

  it("shows the same page when the API itself refuses", async () => {
    signedInAs(admin);
    server.use(http.get("/api/admin/applications", () => problem(403, "Forbidden")));

    renderApp("/admin/applications");

    expect(await screen.findByRole("heading", { name: "You do not have access to this" })).toBeInTheDocument();
  });
});

describe("429", () => {
  it("says when to try again, from Retry-After", async () => {
    signedInAs(admin);
    server.use(http.get("/api/admin/applications", () => HttpResponse.json({ status: 429, title: "Too many attempts", traceId: "t-1" }, { status: 429, headers: { "Retry-After": "75" } })));

    renderApp("/admin/applications");

    expect(await screen.findByText("Too many attempts. Try again in 75 seconds.")).toBeInTheDocument();
    expect(screen.getByText("Reference: t-1")).toBeInTheDocument();
  });
});

describe("the menu", () => {
  const links = () => within(screen.getByRole("navigation", { name: "Main" })).getAllByRole("link").map((link) => link.textContent);

  async function showFor(profile: Profile) {
    signedInAs(profile);
    certificates();
    server.use(http.get("/api/admin/pending-actions", () => HttpResponse.json([])));
    renderApp("/profile");
    await screen.findByRole("navigation", { name: "Main" });
  }

  it("offers an agent their profile and sessions only", async () => {
    await showFor(agent);

    expect(links()).toEqual(["My profile", "My sessions"]);
  });

  it("offers an admin the admin pages as well", async () => {
    await showFor(admin);

    expect(links()).toEqual(["My profile", "My sessions", "Applications", "Proposals", "Invite staff", "Account actions", "Admins", "Audit log"]);
  });

  it("offers a compliance officer the audit log and no admin actions", async () => {
    await showFor({ ...agent, roles: ["AgencyComplianceOfficer", "RegisteredAgent"] });

    expect(links()).toEqual(["My profile", "My sessions", "Audit log"]);
  });

  it("offers a case reviewer nothing extra yet", async () => {
    await showFor({ ...agent, membershipNumber: null, agency: null, roles: ["CaseReviewer"] });

    expect(links()).toEqual(["My profile", "My sessions"]);
  });
});

describe("what is limited, and how to fix it", () => {
  it("tells an agent without a current certificate, and where to add one", async () => {
    signedInAs({ ...agent, hasCurrentPractisingCertificate: false });
    certificates();

    renderApp("/profile");

    const banner = await screen.findByText(/You do not have a current practising certificate/);
    expect(banner.closest("[role=status]")).toHaveTextContent(/an admin has to verify it/);
    expect(within(banner.closest("[role=status]")!).getByRole("link", { name: "your profile" })).toHaveAttribute("href", "/profile");
  });

  it("tells an agent whose agency is suspended", async () => {
    signedInAs({ ...agent, agency: { id: agent.agency?.id ?? "x", name: "Kafue Properties", isActive: false } });
    certificates();

    renderApp("/profile");

    expect(await screen.findByText(/Your agency, Kafue Properties, is suspended/)).toBeInTheDocument();
  });

  it("says nothing to staff, who have no certificate or agency", async () => {
    signedInAs(admin);

    renderApp("/profile");

    await screen.findByRole("heading", { name: "My profile" });
    await waitFor(() => { expect(screen.queryByText(/practising certificate/i)).not.toBeInTheDocument(); });
  });

  it("says nothing to an agent in good standing", async () => {
    signedInAs(agent);
    certificates();

    renderApp("/profile");

    await screen.findByText("Ada Mwansa");
    expect(screen.queryByText(/practising certificate\./)).not.toBeInTheDocument();
  });
});

describe("every page", () => {
  it("has the footer saying this is an independent concept", async () => {
    signedOut();

    renderApp("/sign-in");

    expect(await screen.findByText("Independent concept, not affiliated with ZIEA")).toBeInTheDocument();
  });

  it("shows an unknown address as not found, with the footer", async () => {
    signedOut();

    renderApp("/no/such/page");

    expect(await screen.findByRole("heading", { name: "Page not found" })).toBeInTheDocument();
    expect(screen.getByText("Independent concept, not affiliated with ZIEA")).toBeInTheDocument();
  });

  it.each(["/sign-in", "/sign-up", "/staff/setup", "/two-factor/set-up", "/no/such/page"])("carries the notice that all data is fake: %s", async (path) => {
    signedOut();

    renderApp(path);

    expect(await screen.findByText("Demo: all data is fake.")).toBeInTheDocument();
    expect(screen.getByRole("note")).toHaveTextContent("Do not enter real personal details");
  });

  it("carries the same notice once signed in, as an admin or an agent", async () => {
    signedInAs(agent);
    certificates();
    const { unmount } = renderApp("/profile");
    expect(await screen.findByText("Demo: all data is fake.")).toBeInTheDocument();
    unmount();

    signedInAs(admin);
    renderApp("/sessions");
    expect(await screen.findByText("Demo: all data is fake.")).toBeInTheDocument();
  });

  it("carries it on the page shown when something breaks, too", () => {
    const Broken = (): never => {
      throw new Error("a page failed");
    };
    const quiet = vi.spyOn(console, "error").mockImplementation(() => undefined);

    render(
      <ErrorBoundary>
        <Broken />
      </ErrorBoundary>,
    );

    expect(screen.getByRole("heading", { name: "Something went wrong" })).toBeInTheDocument();
    expect(screen.getByText("Demo: all data is fake.")).toBeInTheDocument();
    quiet.mockRestore();
  });
});

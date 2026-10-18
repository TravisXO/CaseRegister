import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { describe, expect, it } from "vitest";
import { admin, agent, fakeApi, problem, recorded, renderApp, resetRecorded, signedInAs } from "../test/helpers";
import { server } from "../test/server";

const certificates = () => server.use(http.get("/api/me/practising-certificates", () => HttpResponse.json([])));

describe("the profile", () => {
  it("shows the previous sign-in in Zambian time, with the city and country and no address", async () => {
    signedInAs({ ...agent, lastSignIn: { at: "2026-10-13T08:05:00+00:00", country: "ZM", city: "Lusaka" } });
    certificates();
    renderApp("/profile");

    expect(await screen.findByText("Last signed in")).toBeInTheDocument();
    expect(screen.getByText("13 Oct 2026, 10:05 (Zambian time), Lusaka, ZM")).toBeInTheDocument();
  });

  it("says so on the first sign-in", async () => {
    signedInAs(agent);
    certificates();
    renderApp("/profile");

    expect(await screen.findByText("This is your first sign-in.")).toBeInTheDocument();
  });

  it("warns when two or fewer recovery codes are left", async () => {
    signedInAs({ ...agent, recoveryCodesRemaining: 2, recoveryCodesLow: true });
    certificates();
    renderApp("/profile");

    expect(await screen.findByRole("alert")).toHaveTextContent("You have 2 recovery codes left. Make new ones now");
  });

  it("does not warn when there are plenty", async () => {
    signedInAs(agent);
    certificates();
    renderApp("/profile");

    expect(await screen.findByText("You have 10 recovery codes left.")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("makes new recovery codes after an authenticator code and shows them once", async () => {
    resetRecorded();
    signedInAs({ ...agent, recoveryCodesRemaining: 1, recoveryCodesLow: true });
    certificates();
    fakeApi({ "POST /api/auth/2fa/recovery-codes": () => ({ recoveryCodes: ["AAAA-BBBB-CCCC-DDDD", "EEEE-FFFF-GGGG-HHHH"] }) });
    renderApp("/profile");

    await userEvent.type(await screen.findByLabelText("Authenticator code, to make new recovery codes"), "123456");
    await userEvent.click(screen.getByRole("button", { name: "Make new recovery codes" }));

    expect(await screen.findByText("AAAA-BBBB-CCCC-DDDD")).toBeInTheDocument();
    expect(screen.getByText(/The old codes no longer work/)).toBeInTheDocument();
    expect(recorded.find((item) => item.path === "/api/auth/2fa/recovery-codes")?.body).toEqual({ code: "123456" });
  });
});

describe("account actions", () => {
  it("finds accounts by name or email and shows the ID to use", async () => {
    signedInAs(admin);
    server.use(http.get("/api/admin/people", ({ request }) => {
      expect(new URL(request.url).searchParams.get("search")).toBe("chanda");

      return HttpResponse.json([{ id: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", fullName: "Chanda Banda", email: "chanda@example.test", status: "Approved", roles: ["RegisteredAgent"] }]);
    }));
    renderApp("/admin/actions");

    await userEvent.type(await screen.findByLabelText("Name or email"), "chanda");
    await userEvent.click(screen.getByRole("button", { name: "Search" }));

    expect(await screen.findByText("Account ID: bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")).toBeInTheDocument();
    expect(screen.getByText("chanda@example.test, approved, Registered agent")).toBeInTheDocument();
  });

  it("asks for at least three characters before searching", async () => {
    resetRecorded();
    signedInAs(admin);
    renderApp("/admin/actions");

    await userEvent.type(await screen.findByLabelText("Name or email"), "ch");
    await userEvent.click(screen.getByRole("button", { name: "Search" }));

    expect((await screen.findAllByText("Type at least 3 characters of a name or an email.")).length).toBeGreaterThan(0);
  });

  it("offers to propose a suspension instead once the day's limit is used", async () => {
    resetRecorded();
    signedInAs(admin);
    const target = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    fakeApi({
      [`POST /api/admin/users/${target}/suspend`]: () => problem(422, "Limit reached", "You have used your immediate removals for now.", { problems: [{ code: "ImmediateLimit", message: "You have already done 10 immediate removals in the last 24 hours." }] }),
      "POST /api/admin/pending-actions": () => ({ id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa" }),
    });
    renderApp("/admin/actions");

    await userEvent.type(await screen.findByLabelText("Account ID", { selector: "#suspend-account" }), target);
    await userEvent.type(document.getElementById("suspend-reason")!, "Repeated complaints about conduct, under review.");
    await userEvent.click(screen.getByRole("button", { name: "Suspend…" }));
    await userEvent.click(await screen.findByRole("button", { name: "Yes, suspend" }));

    await userEvent.click(await screen.findByRole("button", { name: "Propose it instead" }));

    await waitFor(() => { expect(recorded.some((item) => item.path === "/api/admin/pending-actions")).toBe(true); });
    expect(recorded.find((item) => item.path === "/api/admin/pending-actions")?.body).toMatchObject({ type: "SuspendUser", targetUserId: target });
    expect(await screen.findByText(/Proposed instead/)).toBeInTheDocument();
  });
});

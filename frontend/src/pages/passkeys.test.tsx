import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { PendingAction, Schemas } from "../api/types";
import { admin, agent, fakeApi, problem, recorded, renderApp, resetRecorded, signedInAs, signedOut } from "../test/helpers";
import { server } from "../test/server";

type Passkey = Schemas["PasskeyView"];

const laptop: Passkey = { id: "cGFzc2tleS1sYXB0b3A", name: "Work laptop", createdAt: "2026-10-10T08:00:00+00:00", backedUp: true, transports: ["internal"] };
const phone: Passkey = { id: "cGFzc2tleS1waG9uZQ", name: "Phone", createdAt: "2026-10-12T08:00:00+00:00", backedUp: false, transports: ["hybrid"] };

const options = { challenge: "Y2hhbGxlbmdl", rp: { id: "localhost", name: "CaseRegister" }, rpId: "localhost" };

/** What the browser has to offer: the two JSON methods and the two calls. Each credential it makes or finds is a stand-in with a toJSON. */
function installBrowser(behaviour: { create?: () => Promise<unknown>; get?: () => Promise<unknown> } = {}) {
  class FakeCredential {
    constructor(private readonly json: object) {}

    toJSON() {
      return this.json;
    }

    static parseCreationOptionsFromJSON(value: unknown) {
      return value;
    }

    static parseRequestOptionsFromJSON(value: unknown) {
      return value;
    }
  }

  const create = vi.fn(behaviour.create ?? (() => Promise.resolve(new FakeCredential({ id: "new", response: { attestationObject: "x" } }))));
  const get = vi.fn(behaviour.get ?? (() => Promise.resolve(new FakeCredential({ id: "found", response: { signature: "y" } }))));
  vi.stubGlobal("PublicKeyCredential", FakeCredential);
  Object.defineProperty(navigator, "credentials", { value: { create, get }, configurable: true });

  return { create, get };
}

afterEach(() => {
  vi.unstubAllGlobals();
  Reflect.deleteProperty(navigator, "credentials");
});

const list = (...items: Passkey[]) => server.use(http.get("/api/auth/passkeys", () => HttpResponse.json(items)));

describe("passkeys on the profile", () => {
  it("lists them in a table with a caption and says when there are none", async () => {
    installBrowser();
    signedInAs(agent);
    list(laptop, phone);

    renderApp("/profile");

    const table = await screen.findByRole("table", { name: "Your passkeys" });
    expect(within(table).getByRole("columnheader", { name: "Name" })).toBeInTheDocument();
    expect(within(table).getByRole("rowheader", { name: "Work laptop" })).toBeInTheDocument();
    expect(within(table).getByRole("rowheader", { name: "Phone" })).toBeInTheDocument();
    expect(screen.getByRole("region", { name: "Your passkeys" })).toBeInTheDocument(); // it scrolls inside this on a narrow screen
  });

  it("says so, and makes no request, when the browser cannot make passkeys", async () => {
    signedInAs(agent);

    renderApp("/profile");

    expect(await screen.findByText(/This browser cannot make passkeys/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add passkey" })).not.toBeInTheDocument();
  });

  it("adds a passkey: the browser asks first, then the made credential goes with the name and a code", async () => {
    resetRecorded();
    const { create } = installBrowser();
    signedInAs(agent);
    list();
    fakeApi({
      "POST /api/auth/passkeys/options": () => options,
      "POST /api/auth/passkeys": () => ({ ...laptop, name: "Office" }),
    });
    renderApp("/profile");
    await screen.findByText("You have no passkeys yet.");

    await userEvent.clear(screen.getByLabelText("A name for this passkey"));
    await userEvent.type(screen.getByLabelText("A name for this passkey"), "Office");
    await userEvent.type(within(screen.getByRole("form", { name: "Add a passkey" })).getByLabelText("Code from your authenticator app"), "123456");
    await userEvent.click(screen.getByRole("button", { name: "Add passkey" }));

    expect(await screen.findByText(/^Added Office\./)).toBeInTheDocument();
    expect(create).toHaveBeenCalledOnce();
    const sent = recorded.find((item) => item.method === "POST" && item.path === "/api/auth/passkeys");
    expect(sent?.body).toEqual({ credential: JSON.stringify({ id: "new", response: { attestationObject: "x" } }), name: "Office", twoFactorCode: "123456" });
    expect(sent?.csrf).toMatch(/^csrf-/);
  });

  it("explains a cancelled passkey in words, and sends nothing", async () => {
    resetRecorded();
    installBrowser({ create: () => Promise.reject(new DOMException("denied", "NotAllowedError")) });
    signedInAs(agent);
    list();
    fakeApi({ "POST /api/auth/passkeys/options": () => options });
    renderApp("/profile");
    await screen.findByText("You have no passkeys yet.");

    await userEvent.type(within(screen.getByRole("form", { name: "Add a passkey" })).getByLabelText("Code from your authenticator app"), "123456");
    await userEvent.click(screen.getByRole("button", { name: "Add passkey" }));

    expect(await screen.findByText("The passkey was not added. You can try again.")).toBeInTheDocument();
    expect(recorded.some((item) => item.path === "/api/auth/passkeys" && item.method === "POST")).toBe(false);
  });

  it("needs the code before it even asks the browser", async () => {
    const { create } = installBrowser();
    signedInAs(agent);
    list();
    renderApp("/profile");
    await screen.findByText("You have no passkeys yet.");

    await userEvent.click(screen.getByRole("button", { name: "Add passkey" }));

    expect(await screen.findByRole("link", { name: "Enter a code from your authenticator app." })).toBeInTheDocument();
    expect(create).not.toHaveBeenCalled();
  });

  it("renames a passkey", async () => {
    resetRecorded();
    installBrowser();
    signedInAs(agent);
    list(laptop);
    fakeApi({ [`PATCH /api/auth/passkeys/${laptop.id}`]: () => ({ ...laptop, name: "Desk" }) });
    renderApp("/profile");
    await screen.findByRole("rowheader", { name: "Work laptop" });

    await userEvent.click(screen.getByRole("button", { name: "Rename Work laptop" }));
    const field = screen.getByLabelText("New name");
    await userEvent.clear(field);
    await userEvent.type(field, "Desk");
    await userEvent.click(screen.getByRole("button", { name: "Save name" }));

    expect(await screen.findByText("Passkey renamed.")).toBeInTheDocument();
    expect(recorded.find((item) => item.method === "PATCH")?.body).toEqual({ name: "Desk" });
  });

  it("removes a passkey with a code, and says the other sessions ended", async () => {
    resetRecorded();
    installBrowser();
    signedInAs(agent);
    list(laptop);
    fakeApi({ [`POST /api/auth/passkeys/${laptop.id}/remove`]: () => null });
    renderApp("/profile");
    await screen.findByRole("rowheader", { name: "Work laptop" });

    await userEvent.click(screen.getByRole("button", { name: "Remove Work laptop" }));
    const form = screen.getByRole("form", { name: "Remove Work laptop" });
    await userEvent.type(within(form).getByLabelText("Code from your authenticator app"), "654321");
    await userEvent.click(within(form).getByRole("button", { name: "Remove passkey" }));

    expect(await screen.findByText("Passkey removed. Your other sessions were ended.")).toBeInTheDocument();
    expect(recorded.find((item) => item.path.endsWith("/remove"))?.body).toEqual({ twoFactorCode: "654321" });
  });

  it("offers to remove with another passkey only when there is another", async () => {
    installBrowser();
    signedInAs(agent);
    list(laptop);
    renderApp("/profile");
    await screen.findByRole("rowheader", { name: "Work laptop" });
    await userEvent.click(screen.getByRole("button", { name: "Remove Work laptop" }));
    expect(screen.queryByRole("button", { name: "Remove with another passkey" })).not.toBeInTheDocument();
  });

  it("changes the password with a passkey instead of a code when there is one", async () => {
    resetRecorded();
    const { get } = installBrowser();
    signedInAs(agent);
    list(laptop);
    fakeApi({ "POST /api/auth/passkeys/step-up/options": () => options, "POST /api/auth/change-password": () => null });
    renderApp("/profile");
    await screen.findByRole("button", { name: "Change password with a passkey" });

    await userEvent.type(screen.getByLabelText("Current password"), "old password phrase");
    await userEvent.type(screen.getByLabelText("New password"), "a brand new passphrase");
    await userEvent.click(screen.getByRole("button", { name: "Change password with a passkey" }));

    await waitFor(() => { expect(recorded.some((item) => item.path === "/api/auth/change-password")).toBe(true); });
    expect(get).toHaveBeenCalledOnce();
    expect(recorded.find((item) => item.path === "/api/auth/change-password")?.body).toEqual({
      currentPassword: "old password phrase",
      newPassword: "a brand new passphrase",
      passkeyAssertion: JSON.stringify({ id: "found", response: { signature: "y" } }),
    });
  });

  it("offers no passkey button for a password change to someone who has none", async () => {
    installBrowser();
    signedInAs(agent);
    list();
    renderApp("/profile");
    await screen.findByText("You have no passkeys yet.");
    expect(screen.queryByRole("button", { name: "Change password with a passkey" })).not.toBeInTheDocument();
  });
});

describe("signing in with a passkey", () => {
  it("signs in with no password and no security check, then goes to the profile", async () => {
    resetRecorded();
    const { get } = installBrowser();
    signedOut();
    fakeApi({
      "POST /api/auth/passkey/sign-in/options": () => options,
      "POST /api/auth/passkey/sign-in": () => {
        signedInAs(agent);
        list();

        return { status: "signed-in" };
      },
    });
    renderApp("/sign-in");

    await userEvent.click(await screen.findByRole("button", { name: "Sign in with a passkey" }));

    expect(await screen.findByRole("heading", { name: "My profile" })).toBeInTheDocument();
    await screen.findByText("You have no passkeys yet."); // the page has finished loading what it asks for
    expect(get).toHaveBeenCalledOnce();
    const sent = recorded.find((item) => item.path === "/api/auth/passkey/sign-in");
    expect(sent?.body).toEqual({ credential: JSON.stringify({ id: "found", response: { signature: "y" } }) });
    expect(JSON.stringify(recorded)).not.toContain("turnstile");
  });

  it("says plainly when the passkey was not used, and the password form is still there", async () => {
    installBrowser({ get: () => Promise.reject(new DOMException("denied", "NotAllowedError")) });
    signedOut();
    fakeApi({ "POST /api/auth/passkey/sign-in/options": () => options });
    renderApp("/sign-in");

    await userEvent.click(await screen.findByRole("button", { name: "Sign in with a passkey" }));

    expect(await screen.findByText(/No passkey was used\. You can try again, or use your password and authenticator app\./)).toBeInTheDocument();
    expect(screen.getByLabelText("Email address")).toBeInTheDocument();
  });

  it("shows the server's refusal as a failed sign-in", async () => {
    installBrowser();
    signedOut();
    fakeApi({
      "POST /api/auth/passkey/sign-in/options": () => options,
      "POST /api/auth/passkey/sign-in": () => problem(401, "Sign-in failed", "Sign-in failed. Try again, or use your password and authenticator app."),
    });
    renderApp("/sign-in");

    await userEvent.click(await screen.findByRole("button", { name: "Sign in with a passkey" }));

    expect(await screen.findByText("Sign-in failed. Try again, or use your password and authenticator app.")).toBeInTheDocument();
  });

  it("does not offer a passkey where the browser cannot use one", async () => {
    signedOut();
    renderApp("/sign-in");

    expect(await screen.findByRole("heading", { name: "Sign in" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Sign in with a passkey" })).not.toBeInTheDocument();
  });
});

describe("confirming a proposal with a passkey", () => {
  const hash = "9f2c4e7a1b6d8035c4e1a7f2b9d06e3c58a1f4b7d2e9c06a3f5b8d1e4c7a2f90";
  const action: PendingAction = {
    id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
    type: "SuspendUser",
    target: { userId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", agencyId: null, email: null, name: "Chanda Banda" },
    description: "Suspend the account of Chanda Banda",
    payload: {},
    payloadHash: hash,
    reason: "Left the association; confirmed by phone.",
    status: "Pending",
    proposedBy: { userId: "cccccccc-cccc-4ccc-8ccc-cccccccccccc", name: "Carol Zulu" },
    proposedAt: "2026-10-13T08:05:00+00:00",
    expiresAt: "2026-10-16T08:05:00+00:00",
    decidedByUserId: null,
    decidedAt: null,
    decisionReason: null,
    decidedByName: null,
    lastRefusal: null,
  };

  it("sends the passkey's answer instead of a code", async () => {
    resetRecorded();
    installBrowser();
    signedInAs(admin);
    list(laptop);
    server.use(http.get(`/api/admin/pending-actions/${action.id}`, () => HttpResponse.json(action)));
    fakeApi({ "POST /api/auth/passkeys/step-up/options": () => options, [`POST /api/admin/pending-actions/${action.id}/confirm`]: () => ({ ...action, status: "Confirmed" }) });
    renderApp(`/admin/proposals/${action.id}`);

    await userEvent.click(await screen.findByRole("button", { name: "Confirm with a passkey" }));

    await waitFor(() => { expect(recorded.some((item) => item.path.endsWith("/confirm"))).toBe(true); });
    expect(recorded.find((item) => item.path.endsWith("/confirm"))?.body).toEqual({ payloadHash: hash, passkeyAssertion: JSON.stringify({ id: "found", response: { signature: "y" } }) });
  });
});

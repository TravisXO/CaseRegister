import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { describe, expect, it, vi } from "vitest";
import { agent, fakeApi, problem, recorded, renderApp, resetRecorded } from "../test/helpers";
import { server } from "../test/server";

const otpAuthUri = "otpauth://totp/CaseRegister:ada%40example.test?secret=JBSWY3DPEHPK3PXP&issuer=CaseRegister";
const codes = ["AAAA-1111", "BBBB-2222", "CCCC-3333"];

/** Signed in with a password, no authenticator yet; and once enrolment is confirmed, fully signed in. */
function needsEnrolment() {
  let enrolled = false;
  server.use(
    http.get("/api/auth/session", () => HttpResponse.json({ userId: agent.id, twoFactorComplete: enrolled })),
    http.get("/api/me", () => HttpResponse.json(agent)),
    http.get("/api/me/practising-certificates", () => HttpResponse.json([])),
  );

  return () => {
    enrolled = true;
  };
}

describe("setting up two-factor", () => {
  it("asks nothing of the API until the person starts it", async () => {
    resetRecorded();
    needsEnrolment();
    fakeApi({ "POST /api/auth/2fa/enrol": () => ({ otpAuthUri, sharedKey: "JBSWY3DPEHPK3PXP" }) });

    renderApp("/two-factor/set-up");

    expect(await screen.findByRole("button", { name: "Start set-up" })).toBeInTheDocument();
    expect(recorded.filter((item) => item.path === "/api/auth/2fa/enrol")).toHaveLength(0);
  });

  it("draws the QR code in the browser, shows the key, and sends the secret nowhere", async () => {
    resetRecorded();
    needsEnrolment();
    fakeApi({ "POST /api/auth/2fa/enrol": () => ({ otpAuthUri, sharedKey: "JBSWY3DPEHPK3PXP" }) });
    const seen: string[] = [];
    server.events.on("request:start", ({ request }) => {
      seen.push(request.url);
    });
    renderApp("/two-factor/set-up");

    await userEvent.click(await screen.findByRole("button", { name: "Start set-up" }));

    const picture = await screen.findByAltText("QR code to scan with your authenticator app");
    expect(picture.getAttribute("src")).toMatch(/^data:image\/svg\+xml;base64,/);
    expect(screen.getByText("JBSWY3DPEHPK3PXP")).toBeInTheDocument();
    expect(recorded.filter((item) => item.path === "/api/auth/2fa/enrol")).toHaveLength(1);
    // Only this site was spoken to: no QR service, and the secret is in no address.
    expect(seen.every((address) => new URL(address).origin === window.location.origin)).toBe(true);
    expect(seen.some((address) => address.includes("JBSWY3DPEHPK3PXP"))).toBe(false);
    server.events.removeAllListeners();
  });

  it("starts only once, however often the button is pressed", async () => {
    resetRecorded();
    needsEnrolment();
    fakeApi({ "POST /api/auth/2fa/enrol": () => ({ otpAuthUri, sharedKey: "JBSWY3DPEHPK3PXP" }) });
    renderApp("/two-factor/set-up");
    const button = await screen.findByRole("button", { name: "Start set-up" });

    await userEvent.dblClick(button);

    await screen.findByLabelText("Code from the app");
    expect(recorded.filter((item) => item.path === "/api/auth/2fa/enrol")).toHaveLength(1);
  });

  it("shows the recovery codes once, with copy and download, after the first code is right", async () => {
    resetRecorded();
    const done = needsEnrolment();
    fakeApi({
      "POST /api/auth/2fa/enrol": () => ({ otpAuthUri, sharedKey: "JBSWY3DPEHPK3PXP" }),
      "POST /api/auth/2fa/enrol/confirm": () => {
        done();

        return { recoveryCodes: codes };
      },
    });
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    const createObjectURL = vi.fn(() => "blob:codes");
    Object.assign(URL, { createObjectURL, revokeObjectURL: vi.fn() });
    renderApp("/two-factor/set-up");
    await userEvent.click(await screen.findByRole("button", { name: "Start set-up" }));

    await userEvent.type(await screen.findByLabelText("Code from the app"), "123456");
    await userEvent.click(screen.getByRole("button", { name: "Switch on two-factor" }));

    expect(await screen.findByRole("heading", { name: "Your recovery codes" })).toBeInTheDocument();
    for (const code of codes) {
      expect(screen.getByText(code)).toBeInTheDocument();
    }

    expect(screen.getByText(/This is the only time it is shown/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Copy" }));
    expect(writeText).toHaveBeenCalledWith(codes.join("\n"));
    await userEvent.click(screen.getByRole("button", { name: "Download as a text file" }));
    expect(createObjectURL).toHaveBeenCalledOnce();
    const blob = (createObjectURL.mock.calls[0] as unknown as [Blob])[0];
    expect(await blob.text()).toBe(`${codes.join("\n")}\n`);
  });

  it("warns before the recovery codes are left behind, until they are saved", async () => {
    needsEnrolment();
    fakeApi({
      "POST /api/auth/2fa/enrol": () => ({ otpAuthUri, sharedKey: "JBSWY3DPEHPK3PXP" }),
      "POST /api/auth/2fa/enrol/confirm": () => ({ recoveryCodes: codes }),
    });
    renderApp("/two-factor/set-up");
    await userEvent.click(await screen.findByRole("button", { name: "Start set-up" }));
    await userEvent.type(await screen.findByLabelText("Code from the app"), "123456");
    await userEvent.click(screen.getByRole("button", { name: "Switch on two-factor" }));
    await screen.findByRole("heading", { name: "Your recovery codes" });

    // Closing or reloading the tab. (Waits for the warning to be in place: it is set up just after the codes appear.)
    await waitFor(() => {
      const closing = new Event("beforeunload", { cancelable: true });
      window.dispatchEvent(closing);
      expect(closing.defaultPrevented).toBe(true);
    });

    // Once they are saved, nothing warns.
    expect(screen.getByRole("button", { name: "Carry on" })).toBeDisabled();
    await userEvent.click(screen.getByRole("checkbox", { name: "I have saved this somewhere safe." }));
    const closingAgain = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(closingAgain);
    expect(closingAgain.defaultPrevented).toBe(false);
    expect(screen.getByRole("button", { name: "Carry on" })).toBeEnabled();
  });

  it("goes on to the profile, signed in properly, after the codes are saved", async () => {
    resetRecorded();
    const done = needsEnrolment();
    fakeApi({
      "POST /api/auth/2fa/enrol": () => ({ otpAuthUri, sharedKey: "JBSWY3DPEHPK3PXP" }),
      "POST /api/auth/2fa/enrol/confirm": () => {
        done();

        return { recoveryCodes: codes };
      },
    });
    renderApp("/two-factor/set-up");
    await userEvent.click(await screen.findByRole("button", { name: "Start set-up" }));
    await userEvent.type(await screen.findByLabelText("Code from the app"), "123456");
    await userEvent.click(screen.getByRole("button", { name: "Switch on two-factor" }));
    await screen.findByRole("heading", { name: "Your recovery codes" });

    await userEvent.click(screen.getByRole("checkbox"));
    await userEvent.click(screen.getByRole("button", { name: "Carry on" }));

    expect(await screen.findByRole("heading", { name: "My profile" })).toBeInTheDocument();
    // A new anti-forgery token for the new, fully signed-in session.
    expect(recorded.filter((item) => item.path === "/api/auth/csrf").length).toBeGreaterThanOrEqual(2);
  });

  it("keeps the person here with the reason when the code is wrong", async () => {
    needsEnrolment();
    fakeApi({
      "POST /api/auth/2fa/enrol": () => ({ otpAuthUri, sharedKey: "JBSWY3DPEHPK3PXP" }),
      "POST /api/auth/2fa/enrol/confirm": () => problem(400, "The code was not accepted", "The code is not valid. Check your authenticator app and try again."),
    });
    renderApp("/two-factor/set-up");
    await userEvent.click(await screen.findByRole("button", { name: "Start set-up" }));
    await userEvent.type(await screen.findByLabelText("Code from the app"), "000000");

    await userEvent.click(screen.getByRole("button", { name: "Switch on two-factor" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("The code is not valid. Check your authenticator app and try again.");
    expect(screen.getByLabelText("Code from the app")).toHaveValue("");
    expect(screen.queryByRole("heading", { name: "Your recovery codes" })).not.toBeInTheDocument();
  });

  it("uses one-time-code for the code field", async () => {
    needsEnrolment();
    fakeApi({ "POST /api/auth/2fa/enrol": () => ({ otpAuthUri, sharedKey: "JBSWY3DPEHPK3PXP" }) });
    renderApp("/two-factor/set-up");
    await userEvent.click(await screen.findByRole("button", { name: "Start set-up" }));

    expect(await screen.findByLabelText("Code from the app")).toHaveAttribute("autocomplete", "one-time-code");
  });
});

describe("applying to join", () => {
  const fill = async () => {
    await userEvent.type(await screen.findByLabelText("Full name"), "Ada Mwansa");
    await userEvent.type(screen.getByLabelText("Email address"), "ada@example.test");
    await userEvent.type(screen.getByLabelText("Password"), "a long passphrase for ada");
    await userEvent.type(screen.getByLabelText("Membership number"), "AGT-0101");
    await userEvent.selectOptions(screen.getByLabelText("Class of membership"), "Associate");
    await userEvent.type(screen.getByLabelText("Agency you work for"), "Kafue Properties");
    await userEvent.type(screen.getByLabelText("Practising certificate number"), "9611/2026");
  };

  it("sends the application with the security check, and shows the one answer the API gives", async () => {
    resetRecorded();
    server.use(http.get("/api/auth/session", () => problem(401, "Unauthorized")));
    fakeApi({ "POST /api/auth/sign-up": () => ({ message: "If your details are valid, your application has been submitted." }) });
    renderApp("/sign-up");
    await fill();

    await userEvent.click(screen.getByRole("button", { name: "Send application" }));

    expect(await screen.findByText("If your details are valid, your application has been submitted.")).toBeInTheDocument();
    const call = recorded.find((item) => item.path === "/api/auth/sign-up");
    expect(call?.csrf).toBe("csrf-1");
    expect(call?.body).toMatchObject({ fullName: "Ada Mwansa", membershipClass: "Associate", certificateYear: expect.any(Number) as number, turnstileToken: "token-1" });
  });

  it("puts the API's problems with the fields they are about, and focuses the first", async () => {
    server.use(http.get("/api/auth/session", () => problem(401, "Unauthorized")));
    fakeApi({ "POST /api/auth/sign-up": () => problem(400, "One or more validation errors occurred.", undefined, { errors: { Email: ["Enter a valid email address."], Password: ["Use at least 12 characters."] } }) });
    renderApp("/sign-up");
    await fill();

    await userEvent.click(screen.getByRole("button", { name: "Send application" }));

    const summary = await screen.findByRole("alert");
    expect(summary).toHaveTextContent("Enter a valid email address.");
    await waitFor(() => { expect(screen.getByLabelText("Email address")).toHaveFocus(); });
    expect(screen.getByLabelText("Email address")).toHaveAccessibleDescription("Enter a valid email address.");
    expect(screen.getByLabelText("Password")).toHaveAccessibleDescription(/Use at least 12 characters\./);
  });

  it("uses new-password for the password and lets it be pasted", async () => {
    server.use(http.get("/api/auth/session", () => problem(401, "Unauthorized")));
    renderApp("/sign-up");
    const password = await screen.findByLabelText("Password");

    expect(password).toHaveAttribute("autocomplete", "new-password");
    await userEvent.click(password);
    await userEvent.paste("pasted passphrase");
    expect(password).toHaveValue("pasted passphrase");
  });

  it("does not send an application with fields missing", async () => {
    resetRecorded();
    server.use(http.get("/api/auth/session", () => problem(401, "Unauthorized")));
    fakeApi({ "POST /api/auth/sign-up": () => ({ message: "x" }) });
    renderApp("/sign-up");
    await screen.findByLabelText("Full name");

    await userEvent.click(screen.getByRole("button", { name: "Send application" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Enter your full name.");
    expect(recorded.filter((item) => item.path === "/api/auth/sign-up")).toHaveLength(0);
  });
});

describe("staff setup from an invite link", () => {
  const link = (token: string) => {
    window.history.replaceState(null, "", `/staff/setup#${token}`);
  };

  it("reads the token from the part of the address after the #, and sends it only in the body", async () => {
    resetRecorded();
    server.use(http.get("/api/auth/session", () => problem(401, "Unauthorized")));
    fakeApi({
      "POST /api/auth/staff-setup/start": () => ({ otpAuthUri, sharedKey: "JBSWY3DPEHPK3PXP" }),
      "POST /api/auth/staff-setup/complete": () => ({ recoveryCodes: codes }),
    });
    link("the-invite-token");
    renderApp("/staff/setup");

    await userEvent.click(await screen.findByRole("button", { name: "Start" }));
    await screen.findByAltText("QR code to scan with your authenticator app");
    await userEvent.type(screen.getByLabelText("Password"), "a long passphrase for brian");
    await userEvent.type(screen.getByLabelText("Code from the app"), "123456");
    await userEvent.click(screen.getByRole("button", { name: "Finish set-up" }));

    expect(await screen.findByRole("heading", { name: "Your recovery codes" })).toBeInTheDocument();
    const start = recorded.find((item) => item.path === "/api/auth/staff-setup/start");
    const complete = recorded.find((item) => item.path === "/api/auth/staff-setup/complete");
    expect(start?.body).toEqual({ token: "the-invite-token" });
    expect(complete?.body).toEqual({ token: "the-invite-token", password: "a long passphrase for brian", twoFactorCode: "123456" });
    expect(recorded.every((item) => !item.path.includes("the-invite-token"))).toBe(true); // in no address
  });

  it("offers sign-in once the recovery codes have been saved", async () => {
    server.use(http.get("/api/auth/session", () => problem(401, "Unauthorized")));
    fakeApi({
      "POST /api/auth/staff-setup/start": () => ({ otpAuthUri, sharedKey: "JBSWY3DPEHPK3PXP" }),
      "POST /api/auth/staff-setup/complete": () => ({ recoveryCodes: codes }),
    });
    link("t");
    renderApp("/staff/setup");
    await userEvent.click(await screen.findByRole("button", { name: "Start" }));
    await screen.findByLabelText("Password");
    await userEvent.type(screen.getByLabelText("Password"), "a long passphrase for brian");
    await userEvent.type(screen.getByLabelText("Code from the app"), "123456");
    await userEvent.click(screen.getByRole("button", { name: "Finish set-up" }));
    await screen.findByRole("heading", { name: "Your recovery codes" });

    await userEvent.click(screen.getByRole("checkbox"));
    await userEvent.click(screen.getByRole("button", { name: "Carry on" }));

    expect(await screen.findByText("Your account is ready.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Sign in" })).toHaveAttribute("href", "/sign-in");
  });

  it("says plainly when the link has no token", async () => {
    server.use(http.get("/api/auth/session", () => problem(401, "Unauthorized")));
    window.history.replaceState(null, "", "/staff/setup");
    renderApp("/staff/setup");

    expect(await screen.findByRole("alert")).toHaveTextContent("This page needs the link an admin sent you.");
    expect(screen.queryByRole("button", { name: "Start" })).not.toBeInTheDocument();
  });

  it("shows what the API said, and nothing more, for a link that is not valid", async () => {
    server.use(http.get("/api/auth/session", () => problem(401, "Unauthorized")));
    fakeApi({ "POST /api/auth/staff-setup/start": () => problem(400, "Setup link not valid", "This setup link is not valid. Ask an admin for a new invite.") });
    link("old");
    renderApp("/staff/setup");

    await userEvent.click(await screen.findByRole("button", { name: "Start" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("This setup link is not valid. Ask an admin for a new invite.");
    await waitFor(() => { expect(screen.queryByAltText("QR code to scan with your authenticator app")).not.toBeInTheDocument(); });
  });
});

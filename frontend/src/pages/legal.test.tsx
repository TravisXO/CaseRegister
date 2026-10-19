import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { signedOut, renderApp } from "../test/helpers";

afterEach(() => {
  vi.unstubAllEnvs();
});

describe("the privacy notice", () => {
  it("covers the controller, the purposes with their lawful bases, who it is shared with, how long, the rights and how to ask", async () => {
    signedOut();
    renderApp("/privacy");

    expect(await screen.findByRole("heading", { level: 1, name: "Privacy notice" })).toBeInTheDocument();
    for (const heading of ["Who is responsible for your data", "What is collected, why, and on what basis", "Who it is shared with", "How long it is kept", "Your rights", "How it is protected", "Cookies"]) {
      expect(screen.getByRole("heading", { level: 2, name: heading })).toBeInTheDocument();
    }

    expect(screen.getByText(/the person or organisation running this demonstration/)).toBeInTheDocument(); // the controller
    expect(screen.getAllByText(/Data Protection Act, 2021/).length).toBeGreaterThan(0);
    expect(screen.getByText("Office of the Data Protection Commissioner")).toBeInTheDocument();
    for (const recipient of ["Neon", "Render", "Cloudflare", "Have I Been Pwned"]) {
      expect(screen.getByText(recipient, { selector: "strong" })).toBeInTheDocument();
    }
  });

  it("gives each purpose a lawful basis, and each kind of data a retention time, in tables with captions and headers", async () => {
    signedOut();
    renderApp("/privacy");

    const purposes = await screen.findByRole("table", { name: "What is collected, why, and the lawful basis for each purpose" });
    expect(within(purposes).getAllByRole("columnheader").map((header) => header.textContent)).toEqual(["Purpose", "What is used", "Lawful basis"]);
    for (const row of within(purposes).getAllByRole("row").slice(1)) {
      expect(within(row).getByRole("rowheader")).toBeInTheDocument();
      expect(within(row).getAllByRole("cell")).toHaveLength(2);
      expect(row.textContent).toMatch(/Performance|Legitimate interests|Legal obligation/);
    }

    const keeping = screen.getByRole("table", { name: "How long each kind of data is kept" });
    expect(within(keeping).getByText(/At least 10 years after the relationship ends/)).toBeInTheDocument();
    expect(within(keeping).getByText("12 months from the decision.")).toBeInTheDocument();
    expect(within(keeping).getByText("90 days, unless a critical one has not yet been acknowledged.")).toBeInTheDocument();
    expect(within(keeping).getByText("35 days.")).toBeInTheDocument();
  });

  it("says it is a demo with made-up data, and is not affiliated", async () => {
    signedOut();
    renderApp("/privacy");

    expect(await screen.findByText(/All the data in it is made up/)).toBeInTheDocument();
    expect(screen.getByText(/not affiliated with ZIEA/, { selector: "p" })).toBeInTheDocument();
  });

  it("gives the contact address when one was set when the site was built, and says how to find one when it was not", async () => {
    signedOut();
    const without = renderApp("/privacy");
    expect(await screen.findByText(/use the contact details of the person who gave you this address/)).toBeInTheDocument();
    without.unmount();

    vi.stubEnv("VITE_PRIVACY_CONTACT_EMAIL", "privacy@example.test");
    renderApp("/privacy");
    expect(await screen.findByRole("link", { name: "privacy@example.test" })).toHaveAttribute("href", "mailto:privacy@example.test");
  });

  it("does not use a contact value that is not an email address", async () => {
    signedOut();
    vi.stubEnv("VITE_PRIVACY_CONTACT_EMAIL", "javascript:alert(1)");
    renderApp("/privacy");

    await screen.findByRole("heading", { level: 1, name: "Privacy notice" });
    expect(screen.queryByRole("link", { name: /javascript/ })).not.toBeInTheDocument();
  });
});

describe("the terms of use", () => {
  it("say this is a demo with made-up data only, not affiliated, and not for real compliance work", async () => {
    signedOut();
    renderApp("/terms");

    expect(await screen.findByRole("heading", { level: 1, name: "Terms of use" })).toBeInTheDocument();
    expect(screen.getByText(/a demonstration with made-up data only/)).toBeInTheDocument();
    expect(screen.getByText(/not affiliated with ZIEA/, { selector: "p" })).toBeInTheDocument();
    expect(screen.getByText(/Do not enter real personal data/)).toBeInTheDocument();
    expect(screen.getByText(/Do not use the demonstration for real compliance work/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "privacy notice" })).toHaveAttribute("href", "/privacy");
  });
});

describe("finding them", () => {
  it("links both from the footer of every page, signed in or not", async () => {
    signedOut();
    renderApp("/sign-in");

    const footer = await screen.findByRole("contentinfo");
    expect(within(footer).getByRole("link", { name: "Privacy" })).toHaveAttribute("href", "/privacy");
    expect(within(footer).getByRole("link", { name: "Terms" })).toHaveAttribute("href", "/terms");
    expect(within(footer).getByText(/Independent concept, not affiliated with ZIEA/)).toBeInTheDocument();
  });

  it("links both from the sign-up form, before it is sent, and the link works", async () => {
    signedOut();
    renderApp("/sign-up");

    const privacy = await screen.findByRole("link", { name: "privacy notice" });
    expect(screen.getByRole("link", { name: "terms" })).toHaveAttribute("href", "/terms");
    await userEvent.click(privacy);

    expect(await screen.findByRole("heading", { level: 1, name: "Privacy notice" })).toBeInTheDocument();
  });
});

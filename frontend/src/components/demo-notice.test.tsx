import { screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { describe, expect, it } from "vitest";
import { problem, renderApp, signedOut } from "../test/helpers";
import { server } from "../test/server";

describe("the demo notice", () => {
  it("is shown when the API says this is a demo", async () => {
    signedOut();
    renderApp("/sign-in");

    expect(await screen.findByRole("note")).toHaveTextContent("Demo: all data is fake.");
  });

  it("is shown while the answer has not come, and if it never does", async () => {
    signedOut();
    server.use(http.get("/api/deployment", () => problem(503, "Unavailable")));
    renderApp("/sign-in");

    expect(await screen.findByRole("note")).toHaveTextContent("Demo: all data is fake.");
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.getByRole("note")).toBeInTheDocument();
  });

  it("is driven by the API's answer, so another mode (when there ever is one) would not carry it", async () => {
    signedOut();
    let asked = false;
    server.use(http.get("/api/deployment", () => {
      asked = true;

      return HttpResponse.json({ mode: "Live" });
    }));
    renderApp("/sign-in");

    await waitFor(() => { expect(asked).toBe(true); });
    await waitFor(() => { expect(screen.queryByRole("note")).not.toBeInTheDocument(); });
  });
});

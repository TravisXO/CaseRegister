import { screen } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { describe, expect, it } from "vitest";
import { admin, renderApp, signedInAs } from "../../test/helpers";
import { server } from "../../test/server";

const list = (count: number) => ({
  admins: Array.from({ length: count }, (_, index) => ({ id: `0000000${String(index)}-0000-4000-8000-000000000000`, fullName: `Admin ${String(index)}`, isActive: true, twoFactorOn: true })),
  activeCount: count,
  minimum: 2,
  recommended: 3,
  warning: count < 3,
});

describe("the admin list", () => {
  it("warns while there are fewer than three active admins", async () => {
    signedInAs(admin);
    server.use(http.get("/api/admin/admins", () => HttpResponse.json(list(2))));
    renderApp("/admin/admins");

    expect(await screen.findByTestId("admin-warning")).toHaveTextContent("only 2 active admins");
    expect(screen.getByText("Admin 1")).toBeInTheDocument();
  });

  it("does not warn with three", async () => {
    signedInAs(admin);
    server.use(http.get("/api/admin/admins", () => HttpResponse.json(list(3))));
    renderApp("/admin/admins");

    expect(await screen.findByText("3 active admins.")).toBeInTheDocument();
    expect(screen.queryByTestId("admin-warning")).not.toBeInTheDocument();
  });
});

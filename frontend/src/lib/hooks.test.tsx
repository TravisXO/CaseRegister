import { act, render, screen, waitFor } from "@testing-library/react";
import { RouterProvider, createMemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import { useLeaveWarning } from "./hooks";

function Page({ active }: { active: boolean }) {
  useLeaveWarning(active);

  return <p>Something shown once</p>;
}

function start(active: boolean) {
  const router = createMemoryRouter(
    [
      { path: "/here", element: <Page active={active} /> },
      { path: "/elsewhere", element: <p>Somewhere else</p> },
    ],
    { initialEntries: ["/here"] },
  );
  render(<RouterProvider router={router} />);

  return router;
}

const goTo = (router: ReturnType<typeof start>, path: string) =>
  act(async () => {
    await router.navigate(path);
  });

describe("the warning before leaving a page that will not be shown again", () => {
  it("is raised when the tab is closed or reloaded, and not when there is nothing to lose", async () => {
    const first = start(true);
    await waitFor(() => {
      const closing = new Event("beforeunload", { cancelable: true });
      window.dispatchEvent(closing);
      expect(closing.defaultPrevented).toBe(true);
    });
    expect(first.state.location.pathname).toBe("/here");
  });

  it("is not raised when nothing is shown", () => {
    start(false);

    const closing = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(closing);

    expect(closing.defaultPrevented).toBe(false);
  });

  it("asks before moving to another screen, and 'no' keeps the person where they are", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    const router = start(true);
    await screen.findByText("Something shown once");

    await goTo(router, "/elsewhere");

    await waitFor(() => { expect(confirm).toHaveBeenCalledOnce(); });
    expect(router.state.location.pathname).toBe("/here");
    expect(screen.getByText("Something shown once")).toBeInTheDocument();
  });

  it("lets the person go when they say yes", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const router = start(true);
    await screen.findByText("Something shown once");

    await goTo(router, "/elsewhere");

    await waitFor(() => { expect(router.state.location.pathname).toBe("/elsewhere"); });
    expect(screen.getByText("Somewhere else")).toBeInTheDocument();
  });

  it("does not ask when there is nothing to lose", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    const router = start(false);
    await screen.findByText("Something shown once");

    await goTo(router, "/elsewhere");

    await waitFor(() => { expect(router.state.location.pathname).toBe("/elsewhere"); });
    expect(confirm).not.toHaveBeenCalled();
  });
});

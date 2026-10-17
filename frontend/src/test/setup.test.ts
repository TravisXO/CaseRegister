import { unhandledRequests } from "./server";
import { describe, expect, it } from "vitest";

describe("the fake API", () => {
  it("fails a request nothing has answered, so a test cannot pass while the app calls something unexpected", async () => {
    await expect(fetch("/api/nothing-answers-this")).rejects.toThrow();

    // It was written down too, which is what fails any other test that does this by accident. This is the one test that does it on purpose.
    expect(unhandledRequests).toHaveLength(1);
    expect(unhandledRequests[0]).toContain("/api/nothing-answers-this");
    unhandledRequests.length = 0;
  });
});

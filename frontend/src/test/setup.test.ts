import { describe, expect, it } from "vitest";

describe("the fake API", () => {
  it("fails a request nothing has answered, so a test cannot pass while the app calls something unexpected", async () => {
    await expect(fetch("/api/nothing-answers-this")).rejects.toThrow();
  });
});

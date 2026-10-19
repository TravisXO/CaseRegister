import { expectNoAccessibilityViolations } from "./a11y";
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

describe("the accessibility check every screen test ends with", () => {
  it("finds a real problem (a picture with no text alternative, a form field with no label)", async () => {
    const page = document.createElement("main");
    page.innerHTML = '<h1>A page</h1><img src="data:image/gif;base64,R0lGODlhAQABAAAAACw="><input type="text">';
    document.body.append(page);

    await expect(expectNoAccessibilityViolations(page)).rejects.toThrow(/image-alt|alternative text|label/i);

    page.remove();
  });

  it("passes a page that is fine", async () => {
    const page = document.createElement("main");
    page.innerHTML = '<h1>A page</h1><label for="x">Name</label><input id="x" type="text">';
    document.body.append(page);

    await expectNoAccessibilityViolations(page);

    page.remove();
  });
});

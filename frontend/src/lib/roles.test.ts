import { describe, expect, it } from "vitest";
import { roleLabel, rolesText } from "./roles";

describe("the words for roles", () => {
  it.each([
    ["RegisteredAgent", "Registered agent"],
    ["AgencyComplianceOfficer", "Agency compliance officer"],
    ["CaseReviewer", "Case reviewer"],
    ["Admin", "Admin"],
  ])("says %s as %s", (code, label) => {
    expect(roleLabel(code)).toBe(label);
  });

  it("shows anything it does not know as it came, so nothing disappears", () => {
    expect(roleLabel("Something")).toBe("Something");
  });

  it("lists a person's roles in words, or says what to when there are none", () => {
    expect(rolesText(["RegisteredAgent", "AgencyComplianceOfficer"])).toBe("Registered agent, Agency compliance officer");
    expect(rolesText([])).toBe("None yet");
    expect(rolesText([], "no roles")).toBe("no roles");
  });
});

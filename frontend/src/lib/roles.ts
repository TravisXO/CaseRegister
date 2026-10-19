import type { Role } from "../api/types";

/** The words for roles, in one place: every screen that names a role uses this (the server has the same map in RoleLabels.cs). */
const labels: Record<Role, string> = {
  RegisteredAgent: "Registered agent",
  AgencyComplianceOfficer: "Agency compliance officer",
  CaseReviewer: "Case reviewer",
  Admin: "Admin",
};

/** A role's plain label. Anything that is not a known role is shown as it came. */
export function roleLabel(code: string): string {
  return (labels as Record<string, string | undefined>)[code] ?? code;
}

/** "Registered agent, Admin": a person's roles in words, or what to say when there are none. */
export function rolesText(codes: readonly string[], none = "None yet"): string {
  return codes.length === 0 ? none : codes.map(roleLabel).join(", ");
}

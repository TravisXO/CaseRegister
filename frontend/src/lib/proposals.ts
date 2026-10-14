import type { ProposeActionCommand } from "../api/types";

/** A proposal body with every field the API expects, so a page only names the ones its action uses. */
export function proposal(type: ProposeActionCommand["type"], reason: string, fields: Partial<ProposeActionCommand> = {}): ProposeActionCommand {
  return { type, targetUserId: null, targetAgencyId: null, role: null, certificateId: null, reason: reason.trim(), ...fields };
}

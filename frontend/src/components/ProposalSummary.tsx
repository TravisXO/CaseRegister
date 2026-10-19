import type { PendingAction } from "../api/types";
import { roleLabel } from "../lib/roles";
import { formatRelative, formatZambianTime } from "../lib/time";

const flagText: Record<string, string> = {
  Reapplication: "This person was rejected before and has applied again.",
  DuplicateMembershipNumber: "Another account or another waiting application claims the same membership number.",
};

/** What a decided proposal says about who decided it: "Confirmed by ...". Pending and expired ones were decided by nobody. */
const deciderWord: Record<string, string | undefined> = { Confirmed: "Confirmed", Rejected: "Rejected", Withdrawn: "Withdrawn" };

export function describeFlag(flag: string): string {
  return flagText[flag] ?? flag;
}

interface PayloadFacts {
  agency?: string;
  flags: string[];
  invite?: { email: string; fullName: string; role: string };
  role?: string;
}

/** The parts of what is proposed that are worth a line of their own, read from the proposal's payload. */
function factsOf(action: PendingAction): PayloadFacts {
  const payload = (typeof action.payload === "object" && action.payload !== null ? action.payload : {}) as Record<string, unknown>;
  const facts: PayloadFacts = { flags: [] };

  if (Array.isArray(payload.acknowledgedFlags)) {
    facts.flags = payload.acknowledgedFlags.filter((flag): flag is string => typeof flag === "string");
  }

  if (typeof payload.newAgencyName === "string") {
    facts.agency = `A new agency called "${payload.newAgencyName}" will be created for them.`;
  }

  if (typeof payload.email === "string" && typeof payload.fullName === "string" && typeof payload.role === "string") {
    facts.invite = { email: payload.email, fullName: payload.fullName, role: payload.role };
  } else if (typeof payload.role === "string") {
    facts.role = payload.role;
  }

  return facts;
}

/**
 * A proposal in plain language, for the admin who has to decide on it: what would be done, to whom, who asked, why, and anything they
 * must acknowledge. The confirming admin approves exactly what is shown here (the page sends this proposal's own hash).
 */
export function ProposalSummary({ action }: { action: PendingAction }) {
  const facts = factsOf(action);
  const decider = deciderWord[action.status] ?? null;

  return (
    <div className="card">
      <p className="mb-3 text-lg font-medium">{action.description}</p>
      <dl className="grid grid-cols-1 gap-x-4 gap-y-2 sm:grid-cols-[11rem_1fr]">
        <dt className="font-medium">Proposed by</dt>
        <dd>
          {action.proposedBy.name ?? "An admin"}, {formatZambianTime(action.proposedAt)} ({formatRelative(action.proposedAt)})
        </dd>
        {decider === null ? null : (
          <>
            <dt className="font-medium">{decider} by</dt>
            <dd>
              {action.decidedByName ?? "An admin"}, {formatZambianTime(action.decidedAt)} ({formatRelative(action.decidedAt)})
            </dd>
          </>
        )}
        <dt className="font-medium">Reason given</dt>
        <dd className="whitespace-pre-wrap break-words">{action.reason}</dd>
        {action.target.name === null ? null : (
          <>
            <dt className="font-medium">About</dt>
            <dd>
              {action.target.name}
              {action.target.email === null ? null : ` (${action.target.email})`}
            </dd>
          </>
        )}
        {facts.agency === undefined ? null : (
          <>
            <dt className="font-medium">Agency</dt>
            <dd>{facts.agency}</dd>
          </>
        )}
        {facts.invite === undefined ? null : (
          <>
            <dt className="font-medium">Invited as</dt>
            <dd>
              {facts.invite.fullName}, {facts.invite.email}, as {roleLabel(facts.invite.role)}
            </dd>
          </>
        )}
        {facts.role === undefined ? null : (
          <>
            <dt className="font-medium">Role</dt>
            <dd>{roleLabel(facts.role)}</dd>
          </>
        )}
        {facts.flags.length === 0 ? null : (
          <>
            <dt className="font-medium">Flags acknowledged</dt>
            <dd>
              <ul className="list-inside list-disc">
                {facts.flags.map((flag) => (
                  <li key={flag}>{describeFlag(flag)}</li>
                ))}
              </ul>
            </dd>
          </>
        )}
        <dt className="font-medium">Expires</dt>
        <dd>
          {formatZambianTime(action.expiresAt)} ({formatRelative(action.expiresAt)})
        </dd>
        <dt className="font-medium">Status</dt>
        <dd>
          {action.status}
          {action.decisionReason === null ? null : `: ${action.decisionReason}`}
        </dd>
        {action.lastRefusal === null ? null : (
          <>
            <dt className="font-medium">Last refused</dt>
            <dd>{action.lastRefusal.message}</dd>
          </>
        )}
      </dl>
      <p className="mt-3 break-all text-xs text-slate-600">Fingerprint of exactly this proposal: {action.payloadHash.slice(0, 16)}…</p>
      <details className="mt-2 text-xs text-slate-700">
        <summary>Technical details (IDs)</summary>
        <dl className="mt-1 grid grid-cols-1 gap-x-4 sm:grid-cols-[11rem_1fr]">
          <dt>Proposal</dt>
          <dd className="break-all font-mono">{action.id}</dd>
          <dt>Proposer&apos;s account</dt>
          <dd className="break-all font-mono">{action.proposedBy.userId}</dd>
          {action.decidedByUserId === null ? null : (
            <>
              <dt>Decider&apos;s account</dt>
              <dd className="break-all font-mono">{action.decidedByUserId}</dd>
            </>
          )}
          {action.target.userId === null ? null : (
            <>
              <dt>About (account)</dt>
              <dd className="break-all font-mono">{action.target.userId}</dd>
            </>
          )}
          {action.target.agencyId === null ? null : (
            <>
              <dt>About (agency)</dt>
              <dd className="break-all font-mono">{action.target.agencyId}</dd>
            </>
          )}
        </dl>
      </details>
    </div>
  );
}

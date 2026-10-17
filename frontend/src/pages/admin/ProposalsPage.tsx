import { useState } from "react";
import { Link, useParams } from "react-router-dom";
import { ApiError, describeError } from "../../api/client";
import { api } from "../../api/endpoints";
import type { PendingAction } from "../../api/types";
import { ErrorSummary } from "../../components/ErrorSummary";
import { ReasonField, SubmitButton, TextField } from "../../components/Fields";
import { ProposalSummary } from "../../components/ProposalSummary";
import { ShownOnce } from "../../components/ShownOnce";
import { Empty, Loaded } from "../../components/States";
import { useSession } from "../../auth/session";
import { REASON_MAX, REASON_MIN } from "../../lib/config";
import { errorFor, problemsFrom, required, type Problems } from "../../lib/forms";
import { useLoad, useSubmit } from "../../lib/hooks";
import { formatZambianTime } from "../../lib/time";

export function ProposalsPage() {
  const { state, reload } = useLoad(() => api.pendingActions());

  return (
    <div className="page">
      <h1 className="heading">Proposals waiting for a decision</h1>
      <Loaded load={state} retry={reload} what="Loading proposals">
        {(actions) =>
          actions.length === 0 ? (
            <Empty>Nothing is waiting.</Empty>
          ) : (
            <ul className="space-y-3">
              {actions.map((action) => (
                <li key={action.id} className="card">
                  <p className="font-medium">{action.description}</p>
                  <p className="text-sm text-slate-700">
                    Proposed by {action.proposedBy.name ?? "an admin"}, {formatZambianTime(action.proposedAt)}. Expires {formatZambianTime(action.expiresAt)}.
                  </p>
                  <Link to={`/admin/proposals/${action.id}`} className="btn-secondary mt-2">
                    Open
                  </Link>
                </li>
              ))}
            </ul>
          )
        }
      </Loaded>
    </div>
  );
}

function ConfirmForm({ action, onChanged, onLink, onEnrolmentCode }: { action: PendingAction; onChanged: (notice: string | null) => void; onLink: (link: string) => void; onEnrolmentCode: (code: string) => void }) {
  const [code, setCode] = useState("");
  const [problems, setProblems] = useState<Problems>({ problems: [] });

  const submit = useSubmit(async () => {
    const mistakes = required(code, "code", "Enter a code from your authenticator app, or a recovery code.");
    if (mistakes.length > 0) {
      setProblems({ problems: mistakes });

      return;
    }

    try {
      // The hash sent is the one of the proposal on this screen, so what is confirmed is exactly what was read.
      const confirmed = await api.confirm(action.id, { payloadHash: action.payloadHash, twoFactorCode: code.trim() });
      setCode("");
      if (typeof confirmed.setupLink === "string") {
        onLink(confirmed.setupLink);
      }

      if (typeof confirmed.enrolmentCode === "string") {
        onEnrolmentCode(confirmed.enrolmentCode);
      }

      onChanged(null);
    } catch (error) {
      setCode("");
      setProblems(problemsFrom(error));
      if (error instanceof ApiError && error.status === 409) {
        // It changed or was decided meanwhile: show it as it is now, and say why, so it is read again before anything is confirmed.
        onChanged(describeError(error).message);
      }
    }
  });

  return (
    <form
      noValidate
      onSubmit={(event) => {
        event.preventDefault();
        void submit.run();
      }}
    >
      <h2 className="subheading">Confirm this proposal</h2>
      <p className="mb-2">Confirming carries it out at once. You are approving exactly what is shown above.</p>
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      <TextField id="code" label="Code from your authenticator app" autoComplete="one-time-code" inputMode="numeric" value={code} onChange={(event) => { setCode(event.target.value); }} error={errorFor(problems.problems, "code")} />
      <SubmitButton pending={submit.pending}>Confirm</SubmitButton>
    </form>
  );
}

function ReasonForm({ title, button, danger, onSubmit }: { title: string; button: string; danger?: boolean; onSubmit: (reason: string) => Promise<void> }) {
  const [reason, setReason] = useState("");
  const [problems, setProblems] = useState<Problems>({ problems: [] });

  const submit = useSubmit(async () => {
    const length = reason.trim().length;
    if (length < REASON_MIN || length > REASON_MAX) {
      setProblems({ problems: [{ field: "reason", message: `Give a reason of ${REASON_MIN} to ${REASON_MAX} characters.` }] });

      return;
    }

    try {
      await onSubmit(reason.trim());
    } catch (error) {
      setProblems(problemsFrom(error));
    }
  });

  return (
    <form
      noValidate
      onSubmit={(event) => {
        event.preventDefault();
        void submit.run();
      }}
    >
      <h2 className="subheading">{title}</h2>
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      <ReasonField id="reason" value={reason} onChange={setReason} error={errorFor(problems.problems, "reason")} min={REASON_MIN} max={REASON_MAX} />
      <SubmitButton pending={submit.pending} variant={danger === true ? "danger" : "primary"}>
        {button}
      </SubmitButton>
    </form>
  );
}

export function ProposalPage() {
  const { id = "" } = useParams();
  const { state: session } = useSession();
  const { state, reload } = useLoad(() => api.pendingAction(id), [id]);
  const [setupLink, setSetupLink] = useState<string | null>(null);
  const [enrolmentCode, setEnrolmentCode] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const myId = session.status === "ready" ? session.profile.id : null;

  return (
    <div className="page">
      <h1 className="heading">Proposal</h1>
      <p className="mb-4">
        <Link to="/admin/proposals" className="link">
          All proposals
        </Link>
      </p>
      {notice === null ? null : (
        <p role="alert" className="alert-warning">
          {notice}
        </p>
      )}
      <Loaded load={state} retry={reload} what="Loading the proposal">
        {(action) => (
          <>
            <ProposalSummary action={action} />
            {setupLink === null ? null : (
              <div className="mt-4">
                <ShownOnce
                  title="Setup link for the invited person"
                  lines={[setupLink]}
                  fileName="caseregister-setup-link.txt"
                  instructions="Send this link to the person you invited. It works once, for 24 hours."
                  doneLabel="I have sent it"
                  onDone={() => {
                    setSetupLink(null);
                  }}
                />
              </div>
            )}
            {enrolmentCode === null ? null : (
              <div className="mt-4">
                <ShownOnce
                  title="One-time enrolment code"
                  lines={[enrolmentCode]}
                  fileName="caseregister-enrolment-code.txt"
                  instructions="Give this code to the person in person or by phone, never in writing. They need it with their password to set up two-factor again. It works once, for 72 hours."
                  doneLabel="I have given it to them"
                  onDone={() => {
                    setEnrolmentCode(null);
                  }}
                />
              </div>
            )}
            {action.status !== "Pending" ? (
              <p className="mt-4" role="status">
                This proposal is no longer waiting: it is {action.status.toLowerCase()}.
              </p>
            ) : action.proposedBy.userId === myId ? (
              <>
                <p className="alert-info mt-4">You proposed this, so a different admin has to confirm it. You can take it back.</p>
                <ReasonForm
                  title="Withdraw this proposal"
                  button="Withdraw"
                  onSubmit={async (reason) => {
                    await api.withdraw(action.id, reason);
                    reload();
                  }}
                />
              </>
            ) : (
              <>
                <ConfirmForm
                  action={action}
                  onChanged={(message) => {
                    setNotice(message);
                    reload();
                  }}
                  onLink={setSetupLink}
                  onEnrolmentCode={setEnrolmentCode}
                />
                <ReasonForm
                  title="Or reject it"
                  button="Reject"
                  danger
                  onSubmit={async (reason) => {
                    await api.reject(action.id, reason);
                    reload();
                  }}
                />
              </>
            )}
          </>
        )}
      </Loaded>
    </div>
  );
}

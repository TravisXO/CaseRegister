import { useState } from "react";
import { api } from "../../api/endpoints";
import { ErrorSummary } from "../../components/ErrorSummary";
import { ReasonField, SubmitButton, TextField } from "../../components/Fields";
import { REASON_MAX, REASON_MIN } from "../../lib/config";
import { errorFor, problemsFrom, type Problems } from "../../lib/forms";
import { useSubmit } from "../../lib/hooks";

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** One action that takes access away at once. It asks "are you sure?" first, and says exactly what it will do. */
function ImmediateAction({ id, title, button, warning, run }: { id: string; title: string; button: string; warning: (account: string) => string; run: (account: string, reason: string) => Promise<void> }) {
  const [account, setAccount] = useState("");
  const [reason, setReason] = useState("");
  const [asking, setAsking] = useState(false);
  const [done, setDone] = useState<string | null>(null);
  const [problems, setProblems] = useState<Problems>({ problems: [] });

  const check = () => {
    const length = reason.trim().length;
    const mistakes = [
      ...(guid.test(account.trim()) ? [] : [{ field: `${id}-account`, message: "Enter the account ID: a long code like 3f2504e0-4f89-11d3-9a0c-0305e82c3301." }]),
      ...(length < REASON_MIN || length > REASON_MAX ? [{ field: `${id}-reason`, message: `Give a reason of ${REASON_MIN} to ${REASON_MAX} characters.` }] : []),
    ];
    setProblems({ problems: mistakes });
    setDone(null);
    setAsking(mistakes.length === 0);
  };

  const confirm = useSubmit(async () => {
    try {
      await run(account.trim(), reason.trim());
      setDone("Done. It took effect at once and is in the audit log.");
      setAccount("");
      setReason("");
    } catch (error) {
      setProblems(problemsFrom(error));
    } finally {
      setAsking(false);
    }
  });

  return (
    <section className="card mb-6" aria-labelledby={`${id}-title`}>
      <h2 id={`${id}-title`} className="mb-2 text-lg font-semibold">
        {title}
      </h2>
      {done === null ? null : (
        <p role="status" className="alert-success">
          {done}
        </p>
      )}
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      <TextField id={`${id}-account`} label="Account ID" hint="Shown on the application, the proposal and in the audit log." autoComplete="off" spellCheck={false} value={account} onChange={(event) => { setAccount(event.target.value); setAsking(false); }} error={errorFor(problems.problems, `${id}-account`)} />
      <ReasonField id={`${id}-reason`} value={reason} onChange={(value) => { setReason(value); setAsking(false); }} error={errorFor(problems.problems, `${id}-reason`)} min={REASON_MIN} max={REASON_MAX} />
      {asking ? (
        <div role="alertdialog" aria-labelledby={`${id}-sure`} className="alert-warning">
          <p id={`${id}-sure`} className="mb-2 font-medium">
            Are you sure?
          </p>
          <p className="mb-3">{warning(account.trim())}</p>
          <div className="flex gap-2">
            <form
              onSubmit={(event) => {
                event.preventDefault();
                void confirm.run();
              }}
            >
              <SubmitButton pending={confirm.pending} variant="danger">
                Yes, {button.toLowerCase()}
              </SubmitButton>
            </form>
            <button
              type="button"
              className="btn-secondary"
              onClick={() => {
                setAsking(false);
              }}
            >
              No, go back
            </button>
          </div>
        </div>
      ) : (
        <button type="button" className="btn-danger" onClick={check}>
          {button}…
        </button>
      )}
    </section>
  );
}

export function ActionsPage() {
  return (
    <div className="page">
      <h1 className="heading">Account actions</h1>
      <p className="mb-4">These take access away, so they happen at once and one admin is enough. If the account belongs to an admin, the API refuses and says it has to be proposed to a second admin instead.</p>
      <ImmediateAction
        id="suspend"
        title="Suspend an account"
        button="Suspend"
        warning={(account) => `This suspends the account ${account} and ends all its sessions now.`}
        run={async (account, reason) => {
          await api.suspendUser(account, reason);
        }}
      />
      <ImmediateAction
        id="end-sessions"
        title="End all sessions of an account"
        button="End sessions"
        warning={(account) => `This signs the account ${account} out everywhere now. They can sign in again.`}
        run={async (account, reason) => {
          await api.endUserSessions(account, reason);
        }}
      />
    </div>
  );
}

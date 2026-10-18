import { useState } from "react";
import { Link } from "react-router-dom";
import { ApiError } from "../../api/client";
import { api } from "../../api/endpoints";
import { ErrorSummary } from "../../components/ErrorSummary";
import { ReasonField, SubmitButton, TextField } from "../../components/Fields";
import { IDENTITY_REASON_MIN, REASON_MAX, REASON_MIN } from "../../lib/config";
import { errorFor, problemsFrom, type Problems } from "../../lib/forms";
import { useSubmit } from "../../lib/hooks";
import { proposal } from "../../lib/proposals";
import { rolesText } from "../../lib/roles";
import type { PendingActionType, Person } from "../../api/types";

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** One action that takes access away at once. It asks "are you sure?" first, and says exactly what it will do. */
function ImmediateAction({ id, title, button, warning, run, proposeType }: { id: string; title: string; button: string; warning: (account: string) => string; run: (account: string, reason: string) => Promise<void>; proposeType?: PendingActionType }) {
  const [account, setAccount] = useState("");
  const [reason, setReason] = useState("");
  const [asking, setAsking] = useState(false);
  const [done, setDone] = useState<string | null>(null);
  const [limit, setLimit] = useState(false);
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
      setLimit(false);
    } catch (error) {
      setProblems(problemsFrom(error));
      // Ten at once in a day is the most one admin gets: after that it is proposed, and a second admin confirms it.
      setLimit(error instanceof ApiError && error.problems.some((problem) => problem.code === "ImmediateLimit"));
    } finally {
      setAsking(false);
    }
  });

  const proposeInstead = useSubmit(async () => {
    if (proposeType === undefined) {
      return;
    }

    try {
      const created = await api.propose(proposal(proposeType, reason, { targetUserId: account.trim() }));
      setDone(`Proposed instead. A second admin has to confirm it: /admin/proposals/${created.id}`);
      setLimit(false);
      setProblems({ problems: [] });
      setAccount("");
      setReason("");
    } catch (error) {
      setProblems(problemsFrom(error));
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
      {limit && proposeType !== undefined ? (
        <div className="alert-warning">
          <p className="mb-2">You have used your immediate removals for now. You can propose this instead; a second admin then confirms it.</p>
          <button
            type="button"
            className="btn-secondary"
            onClick={() => {
              void proposeInstead.run();
            }}
          >
            Propose it instead
          </button>
        </div>
      ) : null}
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

/** Finds accounts by name or email, so the right account ID can be copied into the forms below. Each search is recorded in the audit log. */
function FindPeople() {
  const [search, setSearch] = useState("");
  const [results, setResults] = useState<Person[] | null>(null);
  const [problems, setProblems] = useState<Problems>({ problems: [] });

  const find = useSubmit(async () => {
    if (search.trim().length < 3) {
      setProblems({ problems: [{ field: "find-search", message: "Type at least 3 characters of a name or an email." }] });
      setResults(null);

      return;
    }

    try {
      setResults(await api.people(search.trim()));
      setProblems({ problems: [] });
    } catch (error) {
      setResults(null);
      setProblems(problemsFrom(error));
    }
  });

  return (
    <section className="card mb-6" aria-labelledby="find-title">
      <h2 id="find-title" className="mb-2 text-lg font-semibold">
        Find an account
      </h2>
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      <form
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          void find.run();
        }}
      >
        <TextField id="find-search" label="Name or email" autoComplete="off" spellCheck={false} value={search} onChange={(event) => { setSearch(event.target.value); }} error={errorFor(problems.problems, "find-search")} />
        <SubmitButton pending={find.pending}>Search</SubmitButton>
      </form>
      {results === null ? null : results.length === 0 ? (
        <p className="mt-4" role="status">
          No accounts matched.
        </p>
      ) : (
        <ul className="mt-4 space-y-2" aria-label="Accounts found">
          {results.map((person) => (
            <li key={person.id} className="border-t border-slate-300 pt-2">
              <p className="font-medium">{person.fullName}</p>
              <p className="text-sm text-slate-700">
                {person.email}, {person.status.toLowerCase()}, {rolesText(person.roles, "no roles")}
              </p>
              <p className="break-all text-sm text-slate-700">Account ID: {person.id}</p>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/** Lost phone: proposes a reset of somebody's two-factor. A different admin has to confirm, and is shown the one-time code to hand over. */
function ProposeResetCard() {
  const [account, setAccount] = useState("");
  const [reason, setReason] = useState("");
  const [proposed, setProposed] = useState<string | null>(null);
  const [problems, setProblems] = useState<Problems>({ problems: [] });

  const submit = useSubmit(async () => {
    const length = reason.trim().length;
    const mistakes = [
      ...(guid.test(account.trim()) ? [] : [{ field: "reset-account", message: "Enter the account ID: a long code like 3f2504e0-4f89-11d3-9a0c-0305e82c3301." }]),
      ...(length < IDENTITY_REASON_MIN || length > REASON_MAX ? [{ field: "reset-reason", message: `Say how you checked who they are, in ${IDENTITY_REASON_MIN} to ${REASON_MAX} characters.` }] : []),
    ];
    setProblems({ problems: mistakes });
    setProposed(null);
    if (mistakes.length > 0) {
      return;
    }

    try {
      const created = await api.propose(proposal("ResetTwoFactor", reason, { targetUserId: account.trim() }));
      setProposed(created.id);
      setAccount("");
      setReason("");
    } catch (error) {
      setProblems(problemsFrom(error));
    }
  });

  return (
    <section className="card mb-6" aria-labelledby="reset-title">
      <h2 id="reset-title" className="mb-2 text-lg font-semibold">
        Reset someone&apos;s two-factor
      </h2>
      <p className="mb-2">
        For a lost or broken phone. It removes their authenticator and recovery codes and signs them out everywhere. A different admin confirms it and is shown a one-time code to give them in person or by phone. You cannot reset your own. If the person is an admin and there are only two admins, it can only be done from the command line.
      </p>
      {proposed === null ? null : (
        <p role="status" className="alert-success">
          Proposed.{" "}
          <Link to={`/admin/proposals/${proposed}`} className="link">
            Open the proposal
          </Link>
        </p>
      )}
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      <form
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          void submit.run();
        }}
      >
        <TextField id="reset-account" label="Account ID" autoComplete="off" spellCheck={false} value={account} onChange={(event) => { setAccount(event.target.value); }} error={errorFor(problems.problems, "reset-account")} />
        <ReasonField id="reset-reason" value={reason} onChange={setReason} error={errorFor(problems.problems, "reset-reason")} min={IDENTITY_REASON_MIN} max={REASON_MAX} />
        <p className="hint mb-4">The reason must say how you checked the person&apos;s identity: for example, that you saw their ID card in person, or phoned the number held on file.</p>
        <SubmitButton pending={submit.pending}>Propose the reset</SubmitButton>
      </form>
    </section>
  );
}

export function ActionsPage() {
  return (
    <div className="page">
      <h1 className="heading">Account actions</h1>
      <p className="mb-4">These take access away, so they happen at once and one admin is enough. If the account belongs to an admin, the API refuses and says it has to be proposed to a second admin instead.</p>
      <FindPeople />
      <ImmediateAction
        id="suspend"
        title="Suspend an account"
        button="Suspend"
        warning={(account) => `This suspends the account ${account} and ends all its sessions now.`}
        proposeType="SuspendUser"
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
      <ProposeResetCard />
    </div>
  );
}

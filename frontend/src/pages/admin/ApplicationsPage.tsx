import { useState } from "react";
import { Link } from "react-router-dom";
import { api } from "../../api/endpoints";
import type { ApplicationView } from "../../api/types";
import { ErrorSummary } from "../../components/ErrorSummary";
import { ReasonField, SubmitButton, TextField } from "../../components/Fields";
import { describeFlag } from "../../components/ProposalSummary";
import { Empty, Loaded } from "../../components/States";
import { errorFor, problemsFrom, type Problems } from "../../lib/forms";
import { proposal } from "../../lib/proposals";
import { REASON_MAX, REASON_MIN } from "../../lib/config";
import { useLoad, useSubmit } from "../../lib/hooks";
import { formatZambianTime } from "../../lib/time";

function reasonProblem(reason: string): { field: string; message: string }[] {
  const length = reason.trim().length;

  return length < REASON_MIN || length > REASON_MAX ? [{ field: "reason", message: `Give a reason of ${REASON_MIN} to ${REASON_MAX} characters.` }] : [];
}

function Approve({ application, onProposed }: { application: ApplicationView; onProposed: () => void }) {
  const [agency, setAgency] = useState<string>(application.suggestedAgencies[0]?.id ?? "new");
  const [newName, setNewName] = useState(application.agencyName);
  const [acknowledged, setAcknowledged] = useState<string[]>([]);
  const [reason, setReason] = useState("");
  const [problems, setProblems] = useState<Problems>({ problems: [] });

  const submit = useSubmit(async () => {
    const mistakes = [
      ...reasonProblem(reason),
      ...(agency === "new" && newName.trim() === "" ? [{ field: "new-agency", message: "Enter the name of the new agency." }] : []),
      ...(application.flags.some((flag) => !acknowledged.includes(flag)) ? [{ field: "flags", message: "Tick every flag to show you have read it." }] : []),
    ];
    if (mistakes.length > 0) {
      setProblems({ problems: mistakes });

      return;
    }

    try {
      await api.propose(
        proposal("ApproveApplication", reason, {
          targetUserId: application.userId,
          acknowledgedFlags: application.flags,
          ...(agency === "new" ? { newAgencyName: newName.trim() } : { existingAgencyId: agency }),
        }),
      );
      onProposed();
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
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      <fieldset className="mb-4">
        <legend className="label">Which agency do they join?</legend>
        {application.suggestedAgencies.map((match) => (
          <label key={match.id} className="mb-1 flex items-center gap-2">
            <input type="radio" name={`agency-${application.applicationId}`} checked={agency === match.id} onChange={() => { setAgency(match.id); }} />
            <span>Existing agency: {match.name}</span>
          </label>
        ))}
        <label className="mb-1 flex items-center gap-2">
          <input type="radio" name={`agency-${application.applicationId}`} checked={agency === "new"} onChange={() => { setAgency("new"); }} />
          <span>A new agency</span>
        </label>
      </fieldset>
      {agency === "new" ? <TextField id="new-agency" label="Name of the new agency" value={newName} onChange={(event) => { setNewName(event.target.value); }} error={errorFor(problems.problems, "new-agency")} /> : null}
      {application.flags.length === 0 ? null : (
        <fieldset className="mb-4" id="flags" tabIndex={-1}>
          <legend className="label">Flags: read each one, then tick it</legend>
          {application.flags.map((flag) => (
            <label key={flag} className="mb-1 flex items-start gap-2">
              <input
                type="checkbox"
                className="mt-1"
                checked={acknowledged.includes(flag)}
                onChange={(event) => {
                  setAcknowledged((current) => (event.target.checked ? [...current, flag] : current.filter((item) => item !== flag)));
                }}
              />
              <span>{describeFlag(flag)}</span>
            </label>
          ))}
          {errorFor(problems.problems, "flags") === undefined ? null : <span className="field-error">{errorFor(problems.problems, "flags")}</span>}
        </fieldset>
      )}
      <ReasonField id="reason" value={reason} onChange={setReason} error={errorFor(problems.problems, "reason")} min={REASON_MIN} max={REASON_MAX} />
      <SubmitButton pending={submit.pending}>Propose approval</SubmitButton>
    </form>
  );
}

function Reject({ application, onProposed }: { application: ApplicationView; onProposed: () => void }) {
  const [reason, setReason] = useState("");
  const [problems, setProblems] = useState<Problems>({ problems: [] });

  const submit = useSubmit(async () => {
    const mistakes = reasonProblem(reason);
    if (mistakes.length > 0) {
      setProblems({ problems: mistakes });

      return;
    }

    try {
      await api.propose(proposal("RejectApplication", reason, { targetUserId: application.userId }));
      onProposed();
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
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      <ReasonField id="reason" value={reason} onChange={setReason} error={errorFor(problems.problems, "reason")} min={REASON_MIN} max={REASON_MAX} />
      <SubmitButton pending={submit.pending} variant="danger">
        Propose rejection
      </SubmitButton>
    </form>
  );
}

function ApplicationCard({ application }: { application: ApplicationView }) {
  const [mode, setMode] = useState<"approve" | "reject" | null>(null);
  const [proposed, setProposed] = useState<string | null>(null);

  return (
    <li className="card">
      <h2 className="text-lg font-semibold">{application.fullName}</h2>
      <dl className="mb-3 grid grid-cols-1 gap-x-4 gap-y-1 sm:grid-cols-[11rem_1fr]">
        <dt className="font-medium">Email</dt>
        <dd className="break-all">{application.email}</dd>
        <dt className="font-medium">Membership</dt>
        <dd>
          {application.membershipNumber} ({application.membershipClass})
        </dd>
        <dt className="font-medium">Agency they wrote</dt>
        <dd>{application.agencyName}</dd>
        <dt className="font-medium">Certificate</dt>
        <dd>
          {application.certificateNumber} (valid for {application.certificateYear})
        </dd>
        <dt className="font-medium">Applied</dt>
        <dd>{formatZambianTime(application.submittedAt)}</dd>
        <dt className="font-medium">Account ID</dt>
        <dd className="break-all font-mono text-sm">{application.userId}</dd>
      </dl>
      {application.reapplicationCount > 0 ? <p className="mb-2">They have applied {application.reapplicationCount + 1} times.</p> : null}
      {application.flags.length === 0 ? null : (
        <div className="alert-warning" role="note">
          <strong>Check before approving:</strong>
          <ul className="list-inside list-disc">
            {application.flags.map((flag) => (
              <li key={flag}>{describeFlag(flag)}</li>
            ))}
          </ul>
        </div>
      )}
      {proposed === null ? (
        <>
          <div className="mb-3 flex flex-wrap gap-2">
            <button type="button" className="btn-secondary" aria-expanded={mode === "approve"} onClick={() => { setMode(mode === "approve" ? null : "approve"); }}>
              Approve…
            </button>
            <button type="button" className="btn-secondary" aria-expanded={mode === "reject"} onClick={() => { setMode(mode === "reject" ? null : "reject"); }}>
              Reject…
            </button>
          </div>
          {mode === "approve" ? <Approve application={application} onProposed={() => { setProposed("Approval proposed."); }} /> : null}
          {mode === "reject" ? <Reject application={application} onProposed={() => { setProposed("Rejection proposed."); }} /> : null}
        </>
      ) : (
        <p role="status" className="alert-success">
          {proposed} A different admin has to confirm it on the{" "}
          <Link to="/admin/proposals" className="link">
            proposals page
          </Link>
          .
        </p>
      )}
    </li>
  );
}

export function ApplicationsPage() {
  const { state, reload } = useLoad(() => api.applications());

  return (
    <div className="page">
      <h1 className="heading">Applications</h1>
      <p className="mb-4">Approving or rejecting is a proposal: a different admin has to confirm it before anything happens.</p>
      <Loaded load={state} retry={reload} what="Loading applications">
        {(applications) =>
          applications.length === 0 ? (
            <Empty>No applications are waiting.</Empty>
          ) : (
            <ul className="space-y-4">
              {applications.map((application) => (
                <ApplicationCard key={application.applicationId} application={application} />
              ))}
            </ul>
          )
        }
      </Loaded>
    </div>
  );
}

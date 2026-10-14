import { useState } from "react";
import { Link } from "react-router-dom";
import { api } from "../../api/endpoints";
import { ErrorSummary } from "../../components/ErrorSummary";
import { ReasonField, SelectField, SubmitButton, TextField } from "../../components/Fields";
import { REASON_MAX, REASON_MIN } from "../../lib/config";
import { errorFor, problemsFrom, required, type Problems } from "../../lib/forms";
import { proposal } from "../../lib/proposals";
import { useSubmit } from "../../lib/hooks";

export function InviteStaffPage() {
  const [fullName, setFullName] = useState("");
  const [email, setEmail] = useState("");
  const [role, setRole] = useState<"CaseReviewer" | "Admin">("CaseReviewer");
  const [reason, setReason] = useState("");
  const [problems, setProblems] = useState<Problems>({ problems: [] });
  const [proposed, setProposed] = useState(false);

  const submit = useSubmit(async () => {
    const length = reason.trim().length;
    const mistakes = [
      ...required(fullName, "full-name", "Enter their full name."),
      ...required(email, "email", "Enter their email address."),
      ...(length < REASON_MIN || length > REASON_MAX ? [{ field: "reason", message: `Give a reason of ${REASON_MIN} to ${REASON_MAX} characters.` }] : []),
    ];
    if (mistakes.length > 0) {
      setProblems({ problems: mistakes });

      return;
    }

    try {
      await api.propose(proposal("InviteStaff", reason, { role, inviteEmail: email.trim(), inviteFullName: fullName.trim() }));
      setProblems({ problems: [] });
      setProposed(true);
    } catch (error) {
      setProblems(problemsFrom(error, { inviteemail: "email", invitefullname: "full-name" }));
    }
  });

  if (proposed) {
    return (
      <div className="page">
        <h1 className="heading">Invite staff</h1>
        <p role="status" className="alert-success">
          The invitation has been proposed. A different admin has to confirm it; the setup link is shown to them once, when they do, and they send it to {fullName}.
        </p>
        <Link to="/admin/proposals" className="link">
          Go to the proposals
        </Link>
      </div>
    );
  }

  return (
    <div className="page">
      <h1 className="heading">Invite staff</h1>
      <p className="mb-4">For a new admin or case reviewer. They set their own password and authenticator from a one-time link.</p>
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      <form
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          void submit.run();
        }}
      >
        <TextField id="full-name" label="Full name" autoComplete="off" value={fullName} onChange={(event) => { setFullName(event.target.value); }} error={errorFor(problems.problems, "full-name")} />
        <TextField id="email" label="Email address" type="email" autoComplete="off" value={email} onChange={(event) => { setEmail(event.target.value); }} error={errorFor(problems.problems, "email")} />
        <SelectField id="role" label="Role" value={role} onChange={(event) => { setRole(event.target.value as "CaseReviewer" | "Admin"); }}>
          <option value="CaseReviewer">Case reviewer</option>
          <option value="Admin">Admin</option>
        </SelectField>
        <ReasonField id="reason" value={reason} onChange={setReason} error={errorFor(problems.problems, "reason")} min={REASON_MIN} max={REASON_MAX} />
        <SubmitButton pending={submit.pending}>Propose invitation</SubmitButton>
      </form>
    </div>
  );
}

import { useState, type SyntheticEvent } from "react";
import { Link, useNavigate } from "react-router-dom";
import { api } from "../api/endpoints";
import { ErrorSummary } from "../components/ErrorSummary";
import { SubmitButton, TextField } from "../components/Fields";
import { useSession } from "../auth/session";
import { errorFor, problemsFrom, required, type Problems } from "../lib/forms";
import { useSubmit } from "../lib/hooks";

/** After an admin reset this account's two-factor: the one-time code they were given, before setting the authenticator up again. */
export function EnrolmentCodePage() {
  const { refresh } = useSession();
  const navigate = useNavigate();
  const [code, setCode] = useState("");
  const [problems, setProblems] = useState<Problems>({ problems: [] });

  const submit = useSubmit(async () => {
    const mistakes = required(code, "code", "Enter the enrolment code you were given.");
    if (mistakes.length > 0) {
      setProblems({ problems: mistakes });

      return;
    }

    try {
      await api.redeemEnrolmentCode(code.trim());
      await refresh();
      void navigate("/two-factor/set-up", { replace: true });
    } catch (error) {
      setCode("");
      setProblems(problemsFrom(error));
    }
  });

  const onSubmit = (event: SyntheticEvent) => {
    event.preventDefault();
    void submit.run();
  };

  return (
    <div className="page">
      <h1 className="heading">Enrolment code</h1>
      <p className="mb-4">An admin reset your two-factor sign-in. Enter the one-time code they gave you, in person or by phone, to set it up again.</p>
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      <form onSubmit={onSubmit} noValidate>
        <TextField
          id="code"
          label="Enrolment code"
          hint="Twelve letters and numbers, such as AB3D-EF4G-HJ5K. It works once, for 72 hours."
          autoComplete="off"
          autoCapitalize="characters"
          spellCheck={false}
          value={code}
          onChange={(event) => { setCode(event.target.value); }}
          error={errorFor(problems.problems, "code")}
        />
        <SubmitButton pending={submit.pending}>Continue</SubmitButton>
      </form>
      <p className="mt-6">
        <Link to="/sign-in" className="link">
          Back to sign in
        </Link>
      </p>
    </div>
  );
}

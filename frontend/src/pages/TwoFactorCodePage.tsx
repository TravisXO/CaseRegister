import { useState, type SyntheticEvent } from "react";
import { Link, useNavigate } from "react-router-dom";
import { api } from "../api/endpoints";
import { ErrorSummary } from "../components/ErrorSummary";
import { SubmitButton, TextField } from "../components/Fields";
import { useSession } from "../auth/session";
import { errorFor, problemsFrom, required, type Problems } from "../lib/forms";
import { useSubmit } from "../lib/hooks";

/** The second step of signing in: the code from the authenticator app, or one of the recovery codes. */
export function TwoFactorCodePage() {
  const { refresh } = useSession();
  const navigate = useNavigate();
  const [recovery, setRecovery] = useState(false);
  const [code, setCode] = useState("");
  const [problems, setProblems] = useState<Problems>({ problems: [] });

  const submit = useSubmit(async () => {
    const mistakes = required(code, "code", recovery ? "Enter one of your recovery codes." : "Enter the 6-digit code from your authenticator app.");
    if (mistakes.length > 0) {
      setProblems({ problems: mistakes });

      return;
    }

    try {
      await api.verifyTwoFactor(code.trim());
      await refresh(); // a new anti-forgery token, now that someone is signed in
      void navigate("/profile", { replace: true });
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
      <h1 className="heading">Second step</h1>
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      <form onSubmit={onSubmit} noValidate>
        <TextField
          id="code"
          label={recovery ? "Recovery code" : "Code from your authenticator app"}
          hint={recovery ? "Each recovery code works once." : "The 6 digits shown in the app."}
          autoComplete="one-time-code"
          inputMode={recovery ? "text" : "numeric"}
          autoCapitalize="none"
          spellCheck={false}
          value={code}
          onChange={(event) => { setCode(event.target.value); }}
          error={errorFor(problems.problems, "code")}
        />
        <SubmitButton pending={submit.pending}>Continue</SubmitButton>
      </form>
      <p className="mt-6">
        <button
          type="button"
          className="link"
          onClick={() => {
            setRecovery((value) => !value);
            setCode("");
            setProblems({ problems: [] });
          }}
        >
          {recovery ? "Use the code from the app instead" : "Use a recovery code instead"}
        </button>
      </p>
      <p className="mt-2">
        <Link to="/sign-in" className="link">
          Back to sign in
        </Link>
      </p>
    </div>
  );
}

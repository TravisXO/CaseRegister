import { useState, type SyntheticEvent } from "react";
import { Link } from "react-router-dom";
import { api } from "../api/endpoints";
import type { Schemas } from "../api/types";
import { ErrorSummary } from "../components/ErrorSummary";
import { PasswordField, SubmitButton, TextField } from "../components/Fields";
import { QrCode } from "../components/QrCode";
import { ShownOnce } from "../components/ShownOnce";
import { errorFor, problemsFrom, required, type Problems } from "../lib/forms";
import { useSubmit } from "../lib/hooks";

type Step = { name: "start" } | { name: "scan"; enrolment: Schemas["EnrolmentResponse"] } | { name: "codes"; codes: string[] } | { name: "done" };

/** The token in the invite link comes after the "#", which the browser never sends to any server, so it is in no log. */
function tokenFromLink(): string {
  return window.location.hash.replace(/^#/, "");
}

/** An invited member of staff finishing their account: an authenticator, a password, and the first code. */
export function StaffSetupPage() {
  const [token] = useState(tokenFromLink);
  const [step, setStep] = useState<Step>({ name: "start" });
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [problems, setProblems] = useState<Problems>({ problems: [] });

  const start = useSubmit(async () => {
    try {
      setStep({ name: "scan", enrolment: await api.startStaffSetup(token) });
      setProblems({ problems: [] });
    } catch (error) {
      setProblems(problemsFrom(error));
    }
  });

  const complete = useSubmit(async () => {
    const mistakes = [...required(password, "password", "Choose a password."), ...required(code, "code", "Enter the 6-digit code now shown in the app.")];
    if (mistakes.length > 0) {
      setProblems({ problems: mistakes });

      return;
    }

    try {
      const result = await api.completeStaffSetup({ token, password, twoFactorCode: code.trim() });
      setPassword("");
      setStep({ name: "codes", codes: result.recoveryCodes });
      setProblems({ problems: [] });
    } catch (error) {
      setCode("");
      setProblems(problemsFrom(error, { password: "password" }));
    }
  });

  const onComplete = (event: SyntheticEvent) => {
    event.preventDefault();
    void complete.run();
  };

  if (token.length === 0) {
    return (
      <div className="page">
        <h1 className="heading">Set up your account</h1>
        <p role="alert" className="alert-error">
          This page needs the link an admin sent you. Open that link again, in full.
        </p>
      </div>
    );
  }

  return (
    <div className="page">
      <h1 className="heading">Set up your account</h1>
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      {step.name === "start" ? (
        <>
          <p className="mb-4">You have been invited to the register. You will set a password and connect an authenticator app on your phone. The link only works for 24 hours, and once.</p>
          <button
            type="button"
            className="btn-primary"
            disabled={start.pending}
            onClick={() => {
              void start.run();
            }}
          >
            {start.pending ? "Please wait…" : "Start"}
          </button>
        </>
      ) : null}
      {step.name === "scan" ? (
        <>
          <ol className="mb-4 list-inside list-decimal space-y-1">
            <li>Scan this picture with your authenticator app, or type the key into it.</li>
            <li>Choose a password.</li>
            <li>Enter the 6-digit code the app shows.</li>
          </ol>
          <div className="mb-4">
            <QrCode value={step.enrolment.otpAuthUri} />
          </div>
          <p className="mb-4">
            Key: <code className="break-all font-mono">{step.enrolment.sharedKey}</code>
          </p>
          <form onSubmit={onComplete} noValidate>
            <PasswordField id="password" label="Password" autoComplete="new-password" hint="At least 12 characters. A long phrase is best." value={password} onChange={(event) => { setPassword(event.target.value); }} error={errorFor(problems.problems, "password")} />
            <TextField id="code" label="Code from the app" autoComplete="one-time-code" inputMode="numeric" value={code} onChange={(event) => { setCode(event.target.value); }} error={errorFor(problems.problems, "code")} />
            <SubmitButton pending={complete.pending}>Finish set-up</SubmitButton>
          </form>
        </>
      ) : null}
      {step.name === "codes" ? (
        <ShownOnce
          title="Your recovery codes"
          lines={step.codes}
          fileName="caseregister-recovery-codes.txt"
          instructions="If you lose your phone, each of these lets you sign in once. Keep them somewhere safe and private."
          doneLabel="Carry on"
          onDone={() => {
            setStep({ name: "done" });
          }}
        />
      ) : null}
      {step.name === "done" ? (
        <p className="alert-success" role="status">
          Your account is ready.{" "}
          <Link to="/sign-in" className="link">
            Sign in
          </Link>
        </p>
      ) : null}
    </div>
  );
}

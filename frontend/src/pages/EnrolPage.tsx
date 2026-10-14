import { useState, type SyntheticEvent } from "react";
import { Link, Navigate, useNavigate } from "react-router-dom";
import { ApiError } from "../api/client";
import { api } from "../api/endpoints";
import type { Schemas } from "../api/types";
import { ErrorSummary } from "../components/ErrorSummary";
import { SubmitButton, TextField } from "../components/Fields";
import { QrCode } from "../components/QrCode";
import { ShownOnce } from "../components/ShownOnce";
import { Loading } from "../components/States";
import { useSession } from "../auth/session";
import { errorFor, problemsFrom, required, type Problems } from "../lib/forms";
import { useSubmit } from "../lib/hooks";

type Step = { name: "start" } | { name: "scan"; enrolment: Schemas["EnrolmentResponse"] } | { name: "codes"; codes: string[] };

/** Setting up two-factor after signing in. Until it is done, nothing else in the app can be used. */
export function EnrolPage() {
  const { state, refresh } = useSession();
  const navigate = useNavigate();
  const [step, setStep] = useState<Step>({ name: "start" });
  const [code, setCode] = useState("");
  const [problems, setProblems] = useState<Problems>({ problems: [] });
  const [alreadySetUp, setAlreadySetUp] = useState(false);

  // Nothing is requested when the page opens (that would make a key on every visit and every reload): the person asks for it.
  const start = useSubmit(async () => {
    try {
      setStep({ name: "scan", enrolment: await api.startEnrolment() });
      setProblems({ problems: [] });
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        setAlreadySetUp(true);
      }

      setProblems(problemsFrom(error));
    }
  });

  const confirm = useSubmit(async () => {
    const mistakes = required(code, "code", "Enter the 6-digit code now shown in the app.");
    if (mistakes.length > 0) {
      setProblems({ problems: mistakes });

      return;
    }

    try {
      const result = await api.confirmEnrolment(code.trim());
      setStep({ name: "codes", codes: result.recoveryCodes });
      setProblems({ problems: [] });
    } catch (error) {
      setCode("");
      setProblems(problemsFrom(error));
    }
  });

  if (state.status === "loading") {
    return <Loading />;
  }

  if (state.status === "signed-out") {
    return <Navigate to="/sign-in" replace />;
  }

  if (state.status === "ready" && step.name !== "codes") {
    return <Navigate to="/profile" replace />;
  }

  const onConfirm = (event: SyntheticEvent) => {
    event.preventDefault();
    void confirm.run();
  };

  return (
    <div className="page">
      <h1 className="heading">Set up two-factor sign-in</h1>
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      {alreadySetUp ? (
        <p>
          <Link to="/profile" className="link">
            Go to your profile
          </Link>
        </p>
      ) : null}
      {step.name === "start" ? (
        <>
          <p className="mb-4">Every account needs a second step when signing in: a code from an authenticator app on your phone. Install one first (any app that makes 6-digit codes will do).</p>
          <button
            type="button"
            className="btn-primary"
            disabled={start.pending}
            onClick={() => {
              void start.run();
            }}
          >
            {start.pending ? "Please wait…" : "Start set-up"}
          </button>
        </>
      ) : null}
      {step.name === "scan" ? (
        <>
          <ol className="mb-4 list-inside list-decimal space-y-1">
            <li>Scan this picture with your authenticator app.</li>
            <li>Or type this key into the app by hand.</li>
            <li>Enter the 6-digit code the app now shows.</li>
          </ol>
          <div className="mb-4">
            <QrCode value={step.enrolment.otpAuthUri} />
          </div>
          <p className="mb-4">
            Key: <code className="break-all font-mono">{step.enrolment.sharedKey}</code>
          </p>
          <form onSubmit={onConfirm} noValidate>
            <TextField id="code" label="Code from the app" autoComplete="one-time-code" inputMode="numeric" value={code} onChange={(event) => { setCode(event.target.value); }} error={errorFor(problems.problems, "code")} />
            <SubmitButton pending={confirm.pending}>Switch on two-factor</SubmitButton>
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
            void refresh().then(() => navigate("/profile", { replace: true }));
          }}
        />
      ) : null}
    </div>
  );
}

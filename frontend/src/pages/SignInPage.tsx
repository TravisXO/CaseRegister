import { useRef, useState, type SyntheticEvent } from "react";
import { Link, Navigate, useNavigate } from "react-router-dom";
import { api } from "../api/endpoints";
import { ErrorSummary } from "../components/ErrorSummary";
import { PasswordField, SubmitButton, TextField } from "../components/Fields";
import { Turnstile, type TurnstileHandle } from "../components/Turnstile";
import { useSession } from "../auth/session";
import { errorFor, problemsFrom, required, type Problems } from "../lib/forms";
import { useSubmit } from "../lib/hooks";

export function SignInPage() {
  const { state, refresh } = useSession();
  const navigate = useNavigate();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [token, setToken] = useState<string | null>(null);
  const [problems, setProblems] = useState<Problems>({ problems: [] });
  const turnstile = useRef<TurnstileHandle>(null);

  const submit = useSubmit(async () => {
    const mistakes = [
      ...required(email, "email", "Enter your email address."),
      ...required(password, "password", "Enter your password."),
      ...(token === null ? [{ field: "turnstile", message: "Complete the security check." }] : []),
    ];
    if (mistakes.length > 0) {
      setProblems({ problems: mistakes });

      return;
    }

    try {
      const result = await api.signIn({ email: email.trim(), password, turnstileToken: token });
      setPassword("");
      if (result.status === "two-factor-enrolment-required") {
        // Signed in, but with no authenticator yet: the only way on is to set one up.
        await refresh();
        void navigate("/two-factor/set-up", { replace: true });
      } else if (result.status === "enrolment-code-required") {
        // An admin reset the two-factor of this account: the one-time code they were given comes before setting it up again.
        void navigate("/sign-in/enrolment-code", { replace: true });
      } else {
        void navigate("/sign-in/code", { replace: true });
      }
    } catch (error) {
      setProblems(problemsFrom(error));
      turnstile.current?.reset(); // a check works once
    }
  });

  if (state.status === "ready") {
    return <Navigate to="/profile" replace />;
  }

  const notice = state.status === "signed-out" ? state.notice : null;
  const onSubmit = (event: SyntheticEvent) => {
    event.preventDefault();
    void submit.run();
  };

  return (
    <div className="page">
      <h1 className="heading">Sign in</h1>
      {notice === "session-ended" ? (
        <p role="status" className="alert-info">
          Your session ended. Sign in again to carry on.
        </p>
      ) : null}
      {notice === "signed-out" ? (
        <p role="status" className="alert-success">
          You have signed out.
        </p>
      ) : null}
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      <form onSubmit={onSubmit} noValidate>
        <TextField id="email" label="Email address" type="email" autoComplete="username" value={email} onChange={(event) => { setEmail(event.target.value); }} error={errorFor(problems.problems, "email")} />
        <PasswordField id="password" label="Password" autoComplete="current-password" value={password} onChange={(event) => { setPassword(event.target.value); }} error={errorFor(problems.problems, "password")} />
        <Turnstile ref={turnstile} onToken={setToken} />
        {errorFor(problems.problems, "turnstile") === undefined ? null : <p className="field-error mb-4">{errorFor(problems.problems, "turnstile")}</p>}
        <SubmitButton pending={submit.pending}>Sign in</SubmitButton>
      </form>
      <p className="mt-6">
        New here?{" "}
        <Link to="/sign-up" className="link">
          Apply to join
        </Link>
      </p>
    </div>
  );
}

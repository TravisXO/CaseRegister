import { useRef, useState, type SyntheticEvent } from "react";
import { Link } from "react-router-dom";
import { api } from "../api/endpoints";
import type { MembershipClass } from "../api/types";
import { ErrorSummary, type FormProblem } from "../components/ErrorSummary";
import { PasswordField, SelectField, SubmitButton, TextField } from "../components/Fields";
import { Turnstile, type TurnstileHandle } from "../components/Turnstile";
import { currentZambianYear } from "../lib/time";
import { errorFor, problemsFrom, required, type Problems } from "../lib/forms";
import { useSubmit } from "../lib/hooks";

const fields = {
  fullname: "full-name",
  email: "email",
  password: "password",
  membershipnumber: "membership-number",
  membershipclass: "membership-class",
  agencyname: "agency-name",
  certificatenumber: "certificate-number",
  certificateyear: "certificate-year",
  turnstiletoken: "turnstile",
};

const classes: MembershipClass[] = ["Full", "Associate", "Student", "Honorary"];

export function SignUpPage() {
  const [form, setForm] = useState({ fullName: "", email: "", password: "", membershipNumber: "", membershipClass: "Full" as MembershipClass, agencyName: "", certificateNumber: "", certificateYear: String(currentZambianYear()) });
  const [token, setToken] = useState<string | null>(null);
  const [problems, setProblems] = useState<Problems>({ problems: [] });
  const [done, setDone] = useState<string | null>(null);
  const turnstile = useRef<TurnstileHandle>(null);
  const set = (name: keyof typeof form) => (value: string) => {
    setForm((current) => ({ ...current, [name]: value }));
  };

  const submit = useSubmit(async () => {
    const mistakes: FormProblem[] = [
      ...required(form.fullName, "full-name", "Enter your full name."),
      ...required(form.email, "email", "Enter your email address."),
      ...required(form.password, "password", "Choose a password."),
      ...required(form.membershipNumber, "membership-number", "Enter your membership number."),
      ...required(form.agencyName, "agency-name", "Enter the name of your agency."),
      ...required(form.certificateNumber, "certificate-number", "Enter your practising certificate number."),
      ...(/^\d{4}$/.test(form.certificateYear) ? [] : [{ field: "certificate-year", message: "Enter the certificate year as four digits." }]),
      ...(token === null ? [{ field: "turnstile", message: "Complete the security check." }] : []),
    ];
    if (mistakes.length > 0) {
      setProblems({ problems: mistakes });

      return;
    }

    try {
      const result = await api.signUp({
        fullName: form.fullName.trim(),
        email: form.email.trim(),
        password: form.password,
        membershipNumber: form.membershipNumber.trim(),
        membershipClass: form.membershipClass,
        agencyName: form.agencyName.trim(),
        certificateNumber: form.certificateNumber.trim(),
        certificateYear: Number(form.certificateYear),
        turnstileToken: token,
      });
      setDone(result.message);
    } catch (error) {
      setProblems(problemsFrom(error, fields));
      turnstile.current?.reset(); // a check works once
    }
  });

  if (done !== null) {
    return (
      <div className="page">
        <h1 className="heading">Application sent</h1>
        <p className="alert-success" role="status">
          {done}
        </p>
        <p>
          An admin will look at it. You will be able to{" "}
          <Link to="/sign-in" className="link">
            sign in
          </Link>{" "}
          once it has been approved.
        </p>
      </div>
    );
  }

  const onSubmit = (event: SyntheticEvent) => {
    event.preventDefault();
    void submit.run();
  };

  return (
    <div className="page">
      <h1 className="heading">Apply to join</h1>
      <p className="mb-4">For estate agents. An admin checks every application, so you cannot use the register until it has been approved.</p>
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      <form onSubmit={onSubmit} noValidate>
        <TextField id="full-name" label="Full name" autoComplete="name" value={form.fullName} onChange={(event) => { set("fullName")(event.target.value); }} error={errorFor(problems.problems, "full-name")} />
        <TextField id="email" label="Email address" type="email" autoComplete="email" value={form.email} onChange={(event) => { set("email")(event.target.value); }} error={errorFor(problems.problems, "email")} />
        <PasswordField id="password" label="Password" autoComplete="new-password" hint="At least 12 characters. A long phrase is best." value={form.password} onChange={(event) => { set("password")(event.target.value); }} error={errorFor(problems.problems, "password")} />
        <TextField id="membership-number" label="Membership number" value={form.membershipNumber} onChange={(event) => { set("membershipNumber")(event.target.value); }} error={errorFor(problems.problems, "membership-number")} />
        <SelectField id="membership-class" label="Class of membership" value={form.membershipClass} onChange={(event) => { set("membershipClass")(event.target.value); }} error={errorFor(problems.problems, "membership-class")}>
          {classes.map((item) => (
            <option key={item} value={item}>
              {item}
            </option>
          ))}
        </SelectField>
        <TextField id="agency-name" label="Agency you work for" value={form.agencyName} onChange={(event) => { set("agencyName")(event.target.value); }} error={errorFor(problems.problems, "agency-name")} />
        <TextField id="certificate-number" label="Practising certificate number" hint="As printed on the certificate, for example 9611/2026." value={form.certificateNumber} onChange={(event) => { set("certificateNumber")(event.target.value); }} error={errorFor(problems.problems, "certificate-number")} />
        <TextField id="certificate-year" label="Certificate year" inputMode="numeric" value={form.certificateYear} onChange={(event) => { set("certificateYear")(event.target.value); }} error={errorFor(problems.problems, "certificate-year")} />
        <Turnstile ref={turnstile} onToken={setToken} />
        {errorFor(problems.problems, "turnstile") === undefined ? null : <p className="field-error mb-4">{errorFor(problems.problems, "turnstile")}</p>}
        <SubmitButton pending={submit.pending}>Send application</SubmitButton>
      </form>
      <p className="mt-6">
        Already approved?{" "}
        <Link to="/sign-in" className="link">
          Sign in
        </Link>
      </p>
    </div>
  );
}

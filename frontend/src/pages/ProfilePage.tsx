import { useState, type SyntheticEvent } from "react";
import { api } from "../api/endpoints";
import type { CertificateInfo } from "../api/types";
import { ErrorSummary } from "../components/ErrorSummary";
import { PasswordField, SubmitButton, TextField } from "../components/Fields";
import { Empty, Loaded } from "../components/States";
import { useSession } from "../auth/session";
import { errorFor, problemsFrom, required, type Problems } from "../lib/forms";
import { useLoad, useSubmit } from "../lib/hooks";
import { currentZambianYear, formatCalendarDate, formatZambianTime } from "../lib/time";

function certificateStatus(certificate: CertificateInfo): string {
  if (certificate.isCurrent) {
    return "Current and verified";
  }

  return certificate.isVerified ? "Verified, but not for this year" : "Waiting for an admin to verify it";
}

function Certificates({ profileIsMember }: { profileIsMember: boolean }) {
  const { state, reload } = useLoad(() => api.certificates());
  const [number, setNumber] = useState("");
  const [year, setYear] = useState(String(currentZambianYear()));
  const [problems, setProblems] = useState<Problems>({ problems: [] });
  const [added, setAdded] = useState(false);

  const add = useSubmit(async () => {
    const mistakes = [
      ...required(number, "certificate-number", "Enter the certificate number."),
      ...(/^\d{4}$/.test(year) ? [] : [{ field: "certificate-year", message: "Enter the year as four digits." }]),
    ];
    if (mistakes.length > 0) {
      setProblems({ problems: mistakes });
      setAdded(false);

      return;
    }

    try {
      await api.addCertificate({ certificateNumber: number.trim(), validFrom: `${year}-01-01`, validTo: `${year}-12-31` });
      setNumber("");
      setProblems({ problems: [] });
      setAdded(true);
      reload();
    } catch (error) {
      setAdded(false);
      setProblems(problemsFrom(error));
    }
  });

  if (!profileIsMember) {
    return null;
  }

  const onAdd = (event: SyntheticEvent) => {
    event.preventDefault();
    void add.run();
  };

  return (
    <section aria-labelledby="certificates-title">
      <h2 id="certificates-title" className="subheading">
        Practising certificates
      </h2>
      <Loaded load={state} retry={reload} what="Loading your certificates">
        {(certificates) =>
          certificates.length === 0 ? (
            <Empty>You have not added a certificate yet.</Empty>
          ) : (
            <table className="table">
              <thead>
                <tr>
                  <th scope="col">Number</th>
                  <th scope="col">Valid</th>
                  <th scope="col">Status</th>
                </tr>
              </thead>
              <tbody>
                {certificates.map((certificate) => (
                  <tr key={certificate.id}>
                    <td>{certificate.number}</td>
                    <td>
                      {formatCalendarDate(certificate.validFrom)} to {formatCalendarDate(certificate.validTo)}
                    </td>
                    <td>
                      {certificateStatus(certificate)}
                      {certificate.verifiedAt === null ? null : <span className="hint">Verified {formatZambianTime(certificate.verifiedAt)}</span>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )
        }
      </Loaded>
      <h3 className="subheading">Add a certificate</h3>
      {added ? (
        <p role="status" className="alert-success">
          Added. It counts once an admin has verified it.
        </p>
      ) : null}
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      <form onSubmit={onAdd} noValidate>
        <TextField id="certificate-number" label="Certificate number" hint="As printed on the certificate, for example 9611/2026." value={number} onChange={(event) => { setNumber(event.target.value); }} error={errorFor(problems.problems, "certificate-number")} />
        <TextField id="certificate-year" label="Year it is valid for" inputMode="numeric" value={year} onChange={(event) => { setYear(event.target.value); }} error={errorFor(problems.problems, "certificate-year")} />
        <SubmitButton pending={add.pending}>Add certificate</SubmitButton>
      </form>
    </section>
  );
}

function ChangePassword() {
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [code, setCode] = useState("");
  const [problems, setProblems] = useState<Problems>({ problems: [] });
  const [changed, setChanged] = useState(false);

  const change = useSubmit(async () => {
    const mistakes = [
      ...required(current, "current-password", "Enter your current password."),
      ...required(next, "new-password", "Choose a new password."),
      ...required(code, "code", "Enter a code from your authenticator app."),
    ];
    if (mistakes.length > 0) {
      setProblems({ problems: mistakes });
      setChanged(false);

      return;
    }

    try {
      await api.changePassword({ currentPassword: current, newPassword: next, twoFactorCode: code.trim() });
      setCurrent("");
      setNext("");
      setCode("");
      setProblems({ problems: [] });
      setChanged(true);
    } catch (error) {
      setChanged(false);
      setCode("");
      setProblems(problemsFrom(error));
    }
  });

  const onChange = (event: SyntheticEvent) => {
    event.preventDefault();
    void change.run();
  };

  return (
    <section aria-labelledby="password-title">
      <h2 id="password-title" className="subheading">
        Change your password
      </h2>
      {changed ? (
        <p role="status" className="alert-success">
          Your password has been changed.
        </p>
      ) : null}
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      <form onSubmit={onChange} noValidate>
        <PasswordField id="current-password" label="Current password" autoComplete="current-password" value={current} onChange={(event) => { setCurrent(event.target.value); }} error={errorFor(problems.problems, "current-password")} />
        <PasswordField id="new-password" label="New password" autoComplete="new-password" hint="At least 12 characters. A long phrase is best." value={next} onChange={(event) => { setNext(event.target.value); }} error={errorFor(problems.problems, "new-password")} />
        <TextField id="code" label="Code from your authenticator app" autoComplete="one-time-code" inputMode="numeric" value={code} onChange={(event) => { setCode(event.target.value); }} error={errorFor(problems.problems, "code")} />
        <SubmitButton pending={change.pending}>Change password</SubmitButton>
      </form>
    </section>
  );
}

export function ProfilePage() {
  const { state } = useSession();
  if (state.status !== "ready") {
    return null;
  }

  const { profile } = state;

  return (
    <div className="page">
      <h1 className="heading">My profile</h1>
      <dl className="card grid grid-cols-1 gap-x-4 gap-y-2 sm:grid-cols-[12rem_1fr]">
        <dt className="font-medium">Name</dt>
        <dd>{profile.fullName}</dd>
        <dt className="font-medium">Email</dt>
        <dd className="break-all">{profile.email}</dd>
        {profile.membershipNumber === null ? null : (
          <>
            <dt className="font-medium">Membership number</dt>
            <dd>
              {profile.membershipNumber} {profile.membershipClass === null ? null : `(${profile.membershipClass})`}
            </dd>
          </>
        )}
        {profile.agency === null ? null : (
          <>
            <dt className="font-medium">Agency</dt>
            <dd>
              {profile.agency.name}
              {profile.agency.isActive ? "" : " (suspended)"}
            </dd>
          </>
        )}
        <dt className="font-medium">Roles</dt>
        <dd>{profile.roles.length === 0 ? "None yet" : profile.roles.join(", ")}</dd>
      </dl>
      <Certificates profileIsMember={profile.membershipNumber !== null} />
      <ChangePassword />
    </div>
  );
}

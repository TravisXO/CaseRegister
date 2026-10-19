import { useState, type SyntheticEvent } from "react";
import { api } from "../api/endpoints";
import type { CertificateInfo } from "../api/types";
import { ErrorSummary } from "../components/ErrorSummary";
import { PasswordField, SubmitButton, TextField } from "../components/Fields";
import { ShownOnce } from "../components/ShownOnce";
import { Empty, Loaded } from "../components/States";
import { useSession } from "../auth/session";
import { errorFor, problemsFrom, required, type Problems } from "../lib/forms";
import { useLoad, useSubmit } from "../lib/hooks";
import { usePasskeyStepUp } from "../lib/passkeys";
import { PasskeysSection } from "./PasskeysSection";
import { rolesText } from "../lib/roles";
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
  const passkey = usePasskeyStepUp();

  const change = useSubmit(async (how: "code" | "passkey" = "code") => {
    const mistakes = [
      ...required(current, "current-password", "Enter your current password."),
      ...required(next, "new-password", "Choose a new password."),
      ...(how === "passkey" ? [] : required(code, "code", "Enter a code from your authenticator app.")),
    ];
    if (mistakes.length > 0) {
      setProblems({ problems: mistakes });
      setChanged(false);

      return;
    }

    try {
      const proof = how === "passkey" ? { passkeyAssertion: await passkey.answer() } : { twoFactorCode: code.trim() };
      await api.changePassword({ currentPassword: current, newPassword: next, ...proof });
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
        {passkey.available ? (
          <button
            type="button"
            className="btn-secondary ml-2"
            disabled={change.pending}
            onClick={() => {
              void change.run("passkey");
            }}
          >
            Change password with a passkey
          </button>
        ) : null}
      </form>
    </section>
  );
}

/** Recovery codes: how many are left, a warning at two or fewer, and new ones (after a fresh authenticator code). The old ones then stop working. */
function RecoveryCodes({ remaining, low }: { remaining: number; low: boolean }) {
  const { refresh } = useSession();
  const [code, setCode] = useState("");
  const [problems, setProblems] = useState<Problems>({ problems: [] });
  const [codes, setCodes] = useState<string[] | null>(null);

  const make = useSubmit(async () => {
    const mistakes = required(code, "recovery-code", "Enter a code from your authenticator app.");
    if (mistakes.length > 0) {
      setProblems({ problems: mistakes });

      return;
    }

    try {
      const result = await api.regenerateRecoveryCodes(code.trim());
      setCode("");
      setProblems({ problems: [] });
      setCodes(result.recoveryCodes);
    } catch (error) {
      setCode("");
      setProblems(problemsFrom(error));
    }
  });

  const onMake = (event: SyntheticEvent) => {
    event.preventDefault();
    void make.run();
  };

  if (codes !== null) {
    return (
      <section aria-labelledby="recovery-title">
        <ShownOnce
          title="Your new recovery codes"
          lines={codes}
          fileName="caseregister-recovery-codes.txt"
          instructions="The old codes no longer work, and you were signed out of your other sessions. Keep these somewhere safe, apart from your phone."
          doneLabel="I have saved them"
          onDone={() => {
            setCodes(null);
            void refresh();
          }}
        />
      </section>
    );
  }

  return (
    <section aria-labelledby="recovery-title">
      <h2 id="recovery-title" className="subheading">
        Recovery codes
      </h2>
      <p className={low ? "alert-warning" : "mb-4"} role={low ? "alert" : undefined}>
        {low
          ? `You have ${String(remaining)} recovery code${remaining === 1 ? "" : "s"} left. Make new ones now, before you run out.`
          : `You have ${String(remaining)} recovery codes left.`}
      </p>
      <ErrorSummary problems={problems.problems} traceId={problems.traceId} />
      <form onSubmit={onMake} noValidate>
        <TextField id="recovery-code" label="Authenticator code, to make new recovery codes" hint="Making new codes ends your other sessions." autoComplete="one-time-code" inputMode="numeric" value={code} onChange={(event) => { setCode(event.target.value); }} error={errorFor(problems.problems, "recovery-code")} />
        <SubmitButton pending={make.pending}>Make new recovery codes</SubmitButton>
      </form>
    </section>
  );
}

function place(country: string | null | undefined, city: string | null | undefined): string {
  return [city, country].filter((part): part is string => typeof part === "string" && part !== "").join(", ");
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
        <dd>{rolesText(profile.roles)}</dd>
        <dt className="font-medium">Last signed in</dt>
        <dd>
          {profile.lastSignIn === null
            ? "This is your first sign-in."
            : `${formatZambianTime(profile.lastSignIn.at)}${place(profile.lastSignIn.country, profile.lastSignIn.city) === "" ? "" : `, ${place(profile.lastSignIn.country, profile.lastSignIn.city)}`}`}
        </dd>
      </dl>
      <Certificates profileIsMember={profile.membershipNumber !== null} />
      <PasskeysSection />
      <RecoveryCodes remaining={profile.recoveryCodesRemaining} low={profile.recoveryCodesLow} />
      <ChangePassword />
    </div>
  );
}

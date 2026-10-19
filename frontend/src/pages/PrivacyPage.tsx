import { Link } from "react-router-dom";
import { TableRegion } from "../components/TableRegion";
import { privacyContactEmail } from "../lib/config";

/** When the wording was last changed. Change it whenever the text below, or what the service does with data, changes. */
export const PRIVACY_UPDATED = "19 October 2026";

/**
 * The privacy notice, for the Data Protection Act, 2021 of Zambia. It describes what Phase 1 really does with personal data (see
 * docs/ropa.md, which is the record this is written from). It is written for a demonstration with made-up data, and says what the real service
 * would do; a lawyer should read it before any real person's data is held.
 */
export function PrivacyPage() {
  const contact = privacyContactEmail();

  return (
    <div className="page">
      <h1 className="heading">Privacy notice</h1>
      <p className="mb-4 text-sm text-slate-700">Last updated {PRIVACY_UPDATED}.</p>

      <p className="alert-warning" role="note">
        <strong>This is a demonstration. All the data in it is made up.</strong> Please do not enter real personal details, or a password you use anywhere else. This notice says how personal data would be handled, so that it can be checked before anyone&rsquo;s real data is.
      </p>

      <h2 className="subheading">Who is responsible for your data</h2>
      <p className="mb-2">
        The <strong>controller</strong> is the person or organisation running this demonstration (&ldquo;the demo operator&rdquo;), who decides why and how personal data is used. It is an independent project, not run by or for any professional body, and is not affiliated with ZIEA.
      </p>
      <p className="mb-4">
        To reach the demo operator about your data:{" "}
        {contact === undefined ? (
          <>use the contact details of the person who gave you this address.</>
        ) : (
          <a href={`mailto:${contact}`} className="link">
            {contact}
          </a>
        )}
      </p>

      <h2 className="subheading">What is collected, why, and on what basis</h2>
      <p className="mb-2">The Data Protection Act, 2021 requires a lawful basis for each use. These are the ones this service relies on:</p>
      <TableRegion label="Purposes and lawful bases">
        <table className="table">
          <caption className="sr-only">What is collected, why, and the lawful basis for each purpose</caption>
          <thead>
            <tr>
              <th scope="col">Purpose</th>
              <th scope="col">What is used</th>
              <th scope="col">Lawful basis</th>
            </tr>
          </thead>
          <tbody>
            <tr>
              <th scope="row">Applying to join, and running your account</th>
              <td>Name, email address, membership number and class, agency, practising certificate number and dates, the roles you hold.</td>
              <td>Performance of the membership you asked for, and steps you ask for before it.</td>
            </tr>
            <tr>
              <th scope="row">Signing you in safely</th>
              <td>A hash of your password (never the password), your authenticator&rsquo;s key (encrypted), recovery codes (hashed), passkeys (the public part only), your sessions (when, and the browser and address they came from), the country and city of your sign-ins.</td>
              <td>Legitimate interests: keeping accounts, and the records in them, safe from being taken over.</td>
            </tr>
            <tr>
              <th scope="row">Telling you what happened to your account</th>
              <td>Notifications (a proposal waiting, a certificate about to expire, a security notice).</td>
              <td>Performance of the membership; legitimate interests for security notices.</td>
            </tr>
            <tr>
              <th scope="row">Stopping abuse of the sign-in and sign-up forms</th>
              <td>The address a request came from (to limit how many are made), a security check by Cloudflare Turnstile, and a check of a new password against known breaches.</td>
              <td>Legitimate interests: keeping the service available and accounts safe.</td>
            </tr>
            <tr>
              <th scope="row">Keeping an honest record of who did what</th>
              <td>The audit log: what was done, by which account, when, and from which address. Email addresses in it are replaced by a coded form that cannot be turned back into the address without a secret key. Entries are chained so that none can be changed or removed unseen.</td>
              <td>Legal obligation, where the real register keeps anti-money-laundering records; legitimate interests in the integrity of the records.</td>
            </tr>
            <tr>
              <th scope="row">Protecting against loss</th>
              <td>An encrypted copy of the database every night, and a signed fingerprint of the audit log.</td>
              <td>Legitimate interests: not losing the records.</td>
            </tr>
          </tbody>
        </table>
      </TableRegion>
      <p className="mt-2 mb-4">Nothing is used to decide anything about you by machine alone: a risk score, where there is one, only advises a person, who decides and gives a reason. Nothing is sold, and there is no advertising or tracking.</p>

      <h2 className="subheading">Who it is shared with</h2>
      <p className="mb-2">Only the people who run the service and these providers, who handle data on the demo operator&rsquo;s behalf and only for that:</p>
      <ul className="mb-4 list-inside list-disc space-y-1">
        <li>
          <strong>Neon</strong> keeps the database (in Frankfurt, Germany).
        </li>
        <li>
          <strong>Render</strong> runs the service&rsquo;s server (in Frankfurt, Germany).
        </li>
        <li>
          <strong>Cloudflare</strong> serves this website, runs the security check (Turnstile), and stores the audit log&rsquo;s fingerprints and the encrypted backups.
        </li>
        <li>
          <strong>Have I Been Pwned</strong> is asked whether a new password is in a breach. Only the first five characters of a one-way fingerprint of the password are sent, so the password, and who it belongs to, stay unknown to it.
        </li>
      </ul>
      <p className="mb-4">Your data therefore leaves Zambia. It is protected in transit and where it is kept (see below). Within the service, an agency&rsquo;s compliance officer sees only their own agency&rsquo;s records, and an agent only their own.</p>

      <h2 className="subheading">How long it is kept</h2>
      <TableRegion label="How long each kind of data is kept">
        <table className="table">
          <caption className="sr-only">How long each kind of data is kept</caption>
          <thead>
            <tr>
              <th scope="col">What</th>
              <th scope="col">How long</th>
            </tr>
          </thead>
          <tbody>
            <tr>
              <th scope="row">Your account while you are a member</th>
              <td>As long as the membership lasts.</td>
            </tr>
            <tr>
              <th scope="row">Your account after it is suspended, and your agency&rsquo;s record</th>
              <td>At least 10 years after the relationship ends (the Financial Intelligence Centre&rsquo;s guidance on keeping anti-money-laundering records).</td>
            </tr>
            <tr>
              <th scope="row">An application that was turned down</th>
              <td>12 months from the decision.</td>
            </tr>
            <tr>
              <th scope="row">The audit log</th>
              <td>Kept in full: it is a record that must not be changed. It holds no email address in readable form.</td>
            </tr>
            <tr>
              <th scope="row">Sessions</th>
              <td>Until they end (after 30 minutes without use, or 8 hours at most).</td>
            </tr>
            <tr>
              <th scope="row">Notifications</th>
              <td>90 days, unless a critical one has not yet been acknowledged.</td>
            </tr>
            <tr>
              <th scope="row">Backups</th>
              <td>35 days.</td>
            </tr>
          </tbody>
        </table>
      </TableRegion>
      <p className="mt-2 mb-4">Nothing is deleted automatically yet. The service records the date after which a record may be removed, and a hold that stops removal when the law requires a record to be kept; a person has to decide before anything is removed.</p>

      <h2 className="subheading">Your rights</h2>
      <p className="mb-2">Under the Data Protection Act, 2021 you may ask the demo operator to:</p>
      <ul className="mb-4 list-inside list-disc space-y-1">
        <li>tell you whether your data is held, and give you a copy;</li>
        <li>correct data that is wrong or out of date;</li>
        <li>stop or limit a use of your data, or object to it;</li>
        <li>give you your data in a form you can take elsewhere;</li>
        <li>erase your data, where it is no longer needed and no law requires it to be kept (records the law requires to be kept, such as anti-money-laundering records, cannot be erased before their time).</li>
      </ul>
      <p className="mb-4">
        Ask using the contact above. You will be answered, with the reason if the answer is no. If you are not satisfied you may complain to the <strong>Office of the Data Protection Commissioner</strong> of Zambia.
      </p>

      <h2 className="subheading">How it is protected</h2>
      <ul className="mb-4 list-inside list-disc space-y-1">
        <li>Every account has two-factor sign-in, and you can add a passkey, which cannot be handed to a fake site.</li>
        <li>National registration numbers, passport numbers and reports to the Financial Intelligence Centre, where the real register holds them, are encrypted where they are stored.</li>
        <li>Everything travels over an encrypted connection. Backups are encrypted before they leave the server.</li>
        <li>Every view, search and change of a record is written to the audit log.</li>
      </ul>

      <h2 className="subheading">Cookies</h2>
      <p className="mb-4">Only the cookies the service needs to work: the one that keeps you signed in, one that protects forms from being sent by another site, and short-lived ones used during sign-in and when adding a passkey. They are not used to track you, and no analytics or advertising service is used.</p>

      <h2 className="subheading">Changes</h2>
      <p className="mb-4">If this notice changes, the date at the top changes. The rules for using this demonstration are in the <Link to="/terms" className="link">terms</Link>.</p>
    </div>
  );
}

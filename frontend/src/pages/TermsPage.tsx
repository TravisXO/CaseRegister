import { Link } from "react-router-dom";
import { privacyContactEmail } from "../lib/config";

/** When the wording was last changed. */
export const TERMS_UPDATED = "19 October 2026";

/** The terms of using the demonstration. Short on purpose: it is a demo with made-up data, and it says so before anything else. */
export function TermsPage() {
  const contact = privacyContactEmail();

  return (
    <div className="page">
      <h1 className="heading">Terms of use</h1>
      <p className="mb-4 text-sm text-slate-700">Last updated {TERMS_UPDATED}.</p>

      <p className="alert-warning" role="note">
        <strong>This is a demonstration with made-up data only.</strong> It is not the real register of any organisation, and it is not affiliated with ZIEA.
      </p>

      <h2 className="subheading">What this is</h2>
      <p className="mb-4">
        CaseRegister is an independent proposal for a members-only case register and anti-money-laundering tool for estate agents in Zambia. This site lets people try it. Everything in it, every name, number and case, is invented. It is not run by, endorsed by or connected to the Zambia Institute of Estate Agents, the Financial Intelligence Centre or any other body, and nothing in it is a record of any real person or business.
      </p>

      <h2 className="subheading">Using it</h2>
      <ul className="mb-4 list-inside list-disc space-y-2">
        <li>
          <strong>Do not enter real personal data.</strong> Use a made-up name and a made-up membership number, and an email address you do not mind being seen by the people who run the demo. Do not use a password you use anywhere else.
        </li>
        <li>Do not try to break into the service, overload it, or reach anyone else&rsquo;s demo account. If you find a weakness, please report it (see below) instead of using it.</li>
        <li>Do not use the demonstration for real compliance work. It does not give legal advice, and a result in it is not a legal or regulatory conclusion.</li>
        <li>Anything you put in may be seen by the people who run the demo, and may be changed or cleared without notice when the demonstration is reset.</li>
      </ul>

      <h2 className="subheading">Availability and changes</h2>
      <p className="mb-4">The demonstration runs on free plans, so it may be slow to start after being idle, may be unavailable, and may change or end at any time. Nothing is promised about how long it runs or that it is free of mistakes. It is provided as it is, without any warranty, and the people who run it are not responsible for loss that comes from using it, as far as the law allows.</p>

      <h2 className="subheading">Your data</h2>
      <p className="mb-4">
        How personal data is handled is in the <Link to="/privacy" className="link">privacy notice</Link>. If you entered something real by mistake, tell the demo operator so it can be dealt with.
      </p>

      <h2 className="subheading">Reporting a problem</h2>
      <p className="mb-4">
        To report a security problem or to ask about these terms, contact the demo operator
        {contact === undefined ? (
          <> using the details of the person who gave you this address.</>
        ) : (
          <>
            :{" "}
            <a href={`mailto:${contact}`} className="link">
              {contact}
            </a>
            .
          </>
        )}{" "}
        A weakness reported in good faith will not be treated as an attack.
      </p>

      <h2 className="subheading">These terms</h2>
      <p className="mb-4">These terms are for a demonstration and will be replaced by proper ones, written with a lawyer, before the service holds any real person&rsquo;s data.</p>
    </div>
  );
}

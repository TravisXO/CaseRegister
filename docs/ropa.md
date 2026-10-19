# Record of processing activities (Phase 1)

A record of what personal data CaseRegister processes, why, on what basis, who sees it, where it goes and how long it is kept, written for the Data Protection Act, 2021 of Zambia. It describes **what Phase 1 actually does**, not what later phases may do; each new kind of data (cases, the sanctions list, STRs, NRCs) adds its own rows when it is built.

> **Status.** CaseRegister is a demonstration and every record in it is made up. This record exists so that, before any real person's data is held, what happens to it has already been written down and can be checked. The lawful bases and periods are this project's reading of the law and the FIC's 2023 Estate Agents Guidelines; **a lawyer should confirm them** before real use. The public version of the retention and purposes is the privacy notice (`frontend/src/pages/PrivacyPage.tsx`); a test keeps its figures the same as `RetentionPolicy`.

Keep this file in step with the code: a change to what is stored, who sees it, or how long it is kept changes a row here in the same commit.

## 1. The controller and its processors

| Role | Who | Notes |
| --- | --- | --- |
| **Controller** | The demo operator: whoever runs this deployment | Decides the purposes and means. Contact: the address set in `VITE_PRIVACY_CONTACT_EMAIL` when the site is built, shown on the privacy and terms pages. Not affiliated with ZIEA. |
| Processor | **Neon** (PostgreSQL database), Frankfurt | All stored data except the files below. |
| Processor | **Render** (the API), Frankfurt | Handles data in memory while serving requests; logs hold no personal data (see section 5). |
| Processor | **Cloudflare**: Pages (the website and its proxy), Turnstile, R2 (audit anchors and encrypted backups) | A proxy sees requests and responses in passing. R2 holds the audit anchors (hashes only) and the nightly encrypted dumps. |
| Recipient (not a processor) | **Have I Been Pwned** | Receives the first five characters of the SHA-1 of a new password, padded, and nothing else. It cannot tell whose or which password. |
| Processor | **GitHub** (Actions) | Runs the nightly backup and monthly restore check: handles the encrypted dump in transit, never the key that opens it (see `docs/runbooks/backup-and-restore.md`). |

International transfers: Neon and Render are in Germany; Cloudflare serves from many countries. Data therefore leaves Zambia. Protection in transit (TLS everywhere) and at rest (disk encryption by the providers, application-level encryption for secrets, age encryption for backups) is described in section 5.

## 2. Whose data

- **Members and agents** (people who apply to join, and are approved): the data subjects whose accounts, applications and certificates are held.
- **Association staff** (admins, case reviewers): accounts and what they do.
- **Visitors**: anyone who opens the site or tries to sign in or sign up (request addresses, sign-in attempts).

Phase 1 holds no data about the clients of estate agents, no case, no NRC or passport number, no STR and no sanctions list.

## 3. Processing activities

| # | Activity (purpose) | Data subjects | Data | Lawful basis | Recipients | Kept |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | **Applications to join** and the decision | Applicants | Full name, email address, membership number and class, agency name, practising certificate number and year, flags raised by checks, the decision, its reason and who made it | Performance of the membership applied for; steps before it | Admins (two, to decide); the applicant | While the account is current; a **rejected** application 12 months from the decision (`RetentionPolicy`) |
| 2 | **Accounts** and roles | Members, staff | Name, email address, membership number/class, agency, status, roles, `retain_until`, `legal_hold` | Performance of the membership | The person; admins; a compliance officer sees their own agency's members | While the relationship lasts; after suspension at least **10 years** (`retain_until`); agency records likewise |
| 3 | **Practising certificates** | Agents | Certificate number, validity dates, who verified it and when | Performance of the membership; legal obligation (the register may only be used by agents with a current certificate) | The agent; admins; their agency's compliance officer | As the account |
| 4 | **Signing in**: passwords, second factors, passkeys | Everyone with an account | Password hash (PBKDF2, never the password); authenticator key (encrypted with ASP.NET Data Protection); recovery codes and enrolment codes (SHA-256 hashes); passkeys (credential ID, public key, name, counter, device type: no private key); security stamp | Legitimate interests (keeping accounts from being taken over); performance of the membership | The person (names and dates of their own passkeys) | Until removed, reset or replaced. A reset marks the authenticator removed and recovery codes revoked (kept as history); passkeys are deleted on removal or reset (a credential, not a record) |
| 5 | **Sessions** | Signed-in people | Session id, the user, when it started and was last used, the request address and browser | Legitimate interests (security; letting a person see and end their own sessions) | The person (their own list); admins can end sessions | Until the session ends (30 minutes idle, 8 hours at most); the row is deleted when it ends (the one thing hard-deleted, on purpose) |
| 6 | **Sign-in history** and unusual sign-in notices | Signed-in people | Country and city worked out from the address (not the address), time | Legitimate interests (security notices: "was this you?") | The person | Kept with the account |
| 7 | **Lockout and rate limiting** | Visitors, account holders | Account id, request address, failed-attempt counters, lockout level and end | Legitimate interests (stopping password guessing) | Admins are told of lockouts | Counters clear on a full sign-in; lockout history is kept (marked cleared) |
| 8 | **Bot check** (Turnstile) and **breach check** of new passwords | Visitors, people choosing a password | The Turnstile token and request address (to Cloudflare); five characters of a password hash (to HIBP) | Legitimate interests | Cloudflare; Have I Been Pwned | Not stored by CaseRegister |
| 9 | **Notifications** | Members, staff | Text about what happened ("a proposal is waiting", "a passkey was added"), link, read/acknowledged times. No other person's email, ID number or case details | Performance of the membership; legitimate interests for security notices | The recipient only | **90 days**, unless critical and not yet acknowledged |
| 10 | **Admin approvals** (maker-checker) | Targets of an admin action; the admins | The proposal: what, to whom, the reason, who proposed and who decided, when | Legitimate interests (integrity); legal obligation where the real register needs it | Admins | Kept (it is a record of a decision) |
| 11 | **Staff invitations** | Invited staff | Name, email (in the proposal), a hashed one-time setup token, expiry | Steps before the contract of employment or engagement | The inviter; the person | The token is marked used or revoked and kept |
| 12 | **The audit log** | Everyone whose account does or is subject to something | What happened, the account that did it, a role, the target, a request address, short detail, a before/after state with secrets removed. **Email addresses are replaced by `email-hmac:<key id>:<code>`** (HMAC-SHA-256 under a separate secret key), never plain | Legal obligation (anti-money-laundering records and integrity of the register); legitimate interests | Admins see everything; a compliance officer sees their own agency's entries, **never addresses** | Kept in full; it is append-only and hash-chained, so it cannot be edited or have entries removed |
| 13 | **Audit anchors** | (none: hashes only) | A signed fingerprint of the chain at a point in time | Legitimate interests (detecting tampering) | Cloudflare R2 (locked bucket) | Kept (the lock stops deletion) |
| 14 | **Backups** | Everyone in the database | An encrypted copy of the database (`age`, public key in the repository, private key held offline) | Legitimate interests (not losing the records) | Cloudflare R2 (separate bucket); GitHub (in transit) | **35 days** (bucket lifecycle rule) |
| 15 | **Operating the service**: logs | Visitors | Request method, path, status, time, a correlation id and the user id when signed in. **No passwords, tokens, NRCs, report contents or cookie values** (redacted) | Legitimate interests (running and securing the service) | Render | Per Render's log retention on the free plan; not exported |

## 4. Special categories and children

None is processed in Phase 1. Nationality, tribe, religion and gender are not collected, and the risk rules planned for later phases are forbidden from using them on their own (see `CLAUDE.md`). The service is for working estate agents and staff, and is not aimed at children.

## 5. Security measures (Article-style summary)

- **Access**: deny by default (every endpoint needs a signed-in user unless it has to be open); named policies; a record may be read only by those entitled to it (resource checks); admins cannot read FIC reports or change case outcomes; two different admins for approvals.
- **Sign-in**: passwords of at least 12 characters checked against breaches; mandatory TOTP; optional passkeys (phishing-resistant); lockout and rate limits; HttpOnly, Secure, SameSite=Strict cookies; anti-forgery tokens.
- **At rest**: authenticator keys are encrypted by ASP.NET Data Protection, and (in later phases) NRCs, passport numbers and report narratives are to be AES-GCM encrypted under keys from configuration, never code; keys and Data Protection certificates are kept apart from the database, and old ones are kept so nothing becomes unreadable (`docs/runbooks/key-recovery.md`). Backups are encrypted before leaving the server.
- **In transit**: TLS everywhere; strict security headers (CSP, HSTS and the rest).
- **Integrity**: nothing is hard-deleted (sessions and passkeys are credentials, not records); the audit log is hash-chained, anchored outside the database, and verified (`verify-audit`).
- **Privacy by design**: pseudonymised email addresses in the audit log; no tracking, analytics or advertising; generic error messages; nothing sensitive in logs or `localStorage`.
- **Resilience**: nightly backups and a monthly restore check that verifies the audit chain.

## 6. Retention

One class decides it: `CaseRegister.Domain.Common.RetentionPolicy`.

| Record | Rule | Set |
| --- | --- | --- |
| Account of a suspended person; an agency that is suspended | `retain_until` = the date of suspension + **10 years** (the FIC's guidance: AML records at least 10 years after the relationship ends) | On suspension; cleared on reactivation |
| Account whose application was rejected | `retain_until` = decision + **12 months** | On rejection |
| Current or waiting accounts and active agencies | none: nothing is scheduled while a relationship is live | |
| Any record | `legal_hold` stops any removal, whatever the date | By a person, in the database (no screen yet) |

**Nothing is deleted yet.** The dates are recorded so that a removal process, when it is built, starts from dates already on the records, and asks `RetentionPolicy.MayBeConsideredForRemoval` first. Accounts suspended before this was added have no date until they next change status.

## 7. Rights requests

How the rights in the privacy notice are met today (done by a person, using the database owner's access, with each step audited): **access** (the account, its sessions, notifications and the audit entries about it can be exported); **correction** (an admin changes the account; the audit log keeps the history); **objection and restriction** (an admin suspends the account); **erasure** (refused for records within their retention period or under a legal hold, otherwise to be decided by a person; no automated erasure exists). There is no self-service screen for these yet.

## 8. Review

Review this record when a phase adds data or a provider, and at least once a year. A data protection impact assessment is due before the first real person's data, and certainly before NRCs, case data or STRs.

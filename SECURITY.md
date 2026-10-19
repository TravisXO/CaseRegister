# Security policy

CaseRegister is a demonstration with made-up data, but it is built as if the data were real, so a weakness in it matters. Thank you for taking the trouble to report one.

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Use GitHub's private reporting: on the repository's **Security** tab choose **Report a vulnerability**. (If that is not available, write to the contact address on the site's privacy notice and say it is a security report.)

Please include what you found, where (the address or the file), the steps to reproduce it, and what you think it allows. A short note is enough; do not send real personal data, and do not keep or share anything you reached.

## What to expect

| When | What |
| --- | --- |
| Within 3 working days | We acknowledge the report. |
| Within 10 working days | We tell you whether we think it is a vulnerability, and what we plan to do. |
| As soon as it is fixed | We tell you, and credit you if you would like that. |

## Disclosure: 90 days

We ask that you keep the details private for **90 days** from your report, or until a fix has been released, whichever comes first. If a fix needs longer we will say so and agree a date with you. If we have not replied or fixed it after 90 days, you are free to publish your findings. We will not ask you to delay if the problem is already being used against people.

## Safe harbour

We will not take or support action against anyone who reports a vulnerability in good faith, stays within this policy, makes no more than the needed access, does not damage or overload the service, and does not touch other people's data. Testing against the live demonstration is welcome only if you use your own made-up account and do not run automated attacks, denial of service or social engineering.

## What is in scope

The code in this repository and the demonstration site built from it (the API, the website, the pipeline that builds and backs them up). Out of scope: weaknesses of the hosting providers themselves (report those to them), and anything that needs a stolen device or an admin's own credentials.

## Supported versions

Only the latest release and the `main` branch are maintained.

## How the project defends itself

For orientation: sign-in needs a second factor (or a passkey); admin approvals need two different admins; the audit log is hash-chained and anchored outside the database; actions in the build pipeline are pinned to full commits and container images to digests; dependencies are scanned for known vulnerabilities on every change; and each release carries a software bill of materials with a signed provenance attestation.

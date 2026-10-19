# Key recovery and rotation

Four secrets matter more than the rest. This page says where each is kept so it can never be lost, how to replace each one safely, and what the people using the system will notice if one is lost anyway. Nothing here is a key itself; the keys are never written into this repository.

## Where each one is escrowed

Every key below is kept in **two places**, made at the same time as the key:

1. **Your password manager**, as a secure note or attachment named as in the table (with the date it was made and, for the certificate, the date it expires).
2. **A printed copy**, in a sealed envelope signed across the flap, in a fireproof box or a bank safe deposit box that is not in the same building as your computer. For the certificate, print the base64 text and the password on separate sheets in separate envelopes.

A key that is only on Render is not escrowed: Render hides secret values once they are saved, and a rebuilt service starts with none. Check the escrow whenever a key is made or rotated, and once a year read one envelope back against the live system (`verify-keys`, below) to be sure it is still the right one.

| Key | Setting name | Escrow name | Where the live copy is |
| --- | --- | --- | --- |
| Audit HMAC keys (**every one ever used**) | `Audit__Keys__<id>`, current one in `Audit__CurrentKeyId` | "CaseRegister audit key k1" (and k2, k3 ...) | Render |
| Pseudonymisation keys (**every one ever used**) | `Pseudonym__Keys__<id>`, current one in `Pseudonym__CurrentKeyId` | "CaseRegister pseudonym key p1" (and p2 ...) | Render |
| Data protection certificate (current, and every older one still needed) | `DataProtection__CertificateBase64` + `DataProtection__CertificatePassword`; older ones as `DataProtection__Previous__0__CertificateBase64` / `...__CertificatePassword`, `__1__` ... | "CaseRegister data protection certificate 2026" (the .pfx file and its password) | Render |
| Proxy secret | `Proxy__Secret` (and `Proxy__NextSecret` while rotating) on Render, `PROXY_SECRET` on Cloudflare Pages | "CaseRegister proxy secret" | Render and Cloudflare Pages |

None of these keys is ever stored in the database. The audit and pseudonymisation keys and the proxy secret exist only in configuration and in escrow. The data protection **keys** are in the database (table `data_protection_keys`), but they are encrypted with the certificate, so the certificate is what has to be escrowed.

## Checking everything is as it should be

From your machine, in the same environment you use for `seed-admin` (see "Running a command against production" in `docs/deployment.md`):

```powershell
dotnet run --project CaseRegister.Api --configuration Release --no-launch-profile -- verify-keys
```

It loads the configured keys and prints one line for each check, never a key: the audit and pseudonymisation keys that are loaded and which one signs; the data protection certificates; whether any key in `data_protection_keys` cannot be read with them, or has been revoked (neither should ever happen); that the oldest stored authenticator key still decrypts; and that the whole audit chain verifies. It changes nothing and exits with a failure if any check fails. **Run it before and after every rotation below, and after restoring a backup.** If it fails, stop and do not delete anything: the old key you are about to remove may be the one something still needs.

## Audit HMAC key

**What it does.** Every audit entry is signed with the key named on it, so someone with only database access cannot rewrite history and make it look intact. Entries are checked with the key they name.

**Rotate (any time, no downtime).**
1. Make a new key: `.\scripts\New-ProductionKeys.ps1 -OutputDirectory <new folder> -AuditKeyId k2` (keep only `audit-key-k2.txt`; discard the rest). Put it in the password manager and print it **before** it is used.
2. Render: add `Audit__Keys__k2`, set `Audit__CurrentKeyId` = `k2`. **Leave `Audit__Keys__k1` (and every older key) exactly where it is.** Redeploy.
3. Run `verify-keys`. The chain must verify, with entries from both keys.

Only the current key signs; every key ever used is kept to verify. Never remove a retired key, and never reuse an id.

**If it is lost.** The entries it signed can never be verified again. `verify-audit` reports "UnknownKey" at the first of them and stops there, so the chain after it cannot be vouched for either. New entries can still be written with a new key, so the system carries on, but the history before the loss is no longer tamper-evident and the anchors in R2 become the only proof of what it said. Restore the key from escrow before doing anything else.

**What users notice.** Nothing, at the time. An auditor or regulator asking for the log to be proven intact later could not be given that proof.

## Pseudonymisation key

**What it does.** Audit entries that need to refer to an email address (for example a failed sign-in for an unknown address) store a keyed hash of it (HMAC-SHA256), with the id of the key used, instead of the address or a plain hash. Only someone holding the key can test whether an address appears in the log.

**Rotate.** The same as the audit key: make `p2`, print it, add `Pseudonym__Keys__p2`, set `Pseudonym__CurrentKeyId` = `p2`, keep `p1`. New entries use `p2`; old entries keep the key id they were written with. Run `verify-keys`.

**If it is lost.** Old entries still read as they are (they are never decrypted), but nobody can any more test whether a given address appears in an old entry, and the viewer cannot match old entries to an address. Entries written after the loss use a new key. It affects privacy lookups only, not the integrity of the chain.

**What users notice.** Nothing.

## Data protection certificate

**What it does.** The data protection keys in the database protect the session tickets and the authenticator (two-factor) secrets. Those keys are themselves encrypted with this certificate, so without it nothing encrypted by them can be read.

**Rotate (before it expires; the date is in your calendar two months early).**
1. Make a new certificate: `.\scripts\New-ProductionKeys.ps1 -OutputDirectory <new folder>` (keep only the certificate files). Put them in the password manager and print them **before** use.
2. Render: move the **current** values to the first free `Previous` slot (`DataProtection__Previous__0__CertificateBase64` and `DataProtection__Previous__0__CertificatePassword`; the next rotation uses `__1__`, and so on). Put the **new** values in `DataProtection__CertificateBase64` and `DataProtection__CertificatePassword`. Redeploy.
3. Run `verify-keys`. "Data protection key table" must pass and the authenticator sample must decrypt.

New keys are protected with the new certificate only. Older certificates are used only to read keys protected before. Keep each older certificate for as long as any key it protected is in the table (in practice, for ever: nothing in the application ever deletes a key).

The application refuses to start if it cannot read every key in the table. That is deliberate: without the old certificate it would quietly make new keys, and every session and every authenticator secret would become unreadable with nothing to say why. If it will not start with a message about unreadable data protection keys, a certificate that protected them is not configured: put it back as a `Previous` entry.

**If it is lost** (every copy: Render, the password manager and the printed one). The stored data protection keys can never be read again. Every session ends, and **every authenticator secret becomes unreadable: every user would have to set up two-factor again**, which for an admin means a break-glass `reset-two-factor` for each, and for everyone else a reset proposal confirmed by a second admin. Recovery: make a new certificate, delete nothing, let the application create new keys, and reset two-factor for everyone with an authenticator. (This needs a deliberate step: the application refuses to start, as above, so the stored keys would have to be set aside by someone holding the database owner's login.) That is why the certificate is escrowed twice.

**What users notice.** If rotated correctly: nothing. If an old certificate is lost: everyone is signed out and has to set up two-factor again.

## Proxy secret

**What it does.** The Cloudflare Pages Function sends it with every request to prove it is the proxy; the API answers nothing but `/health/live` without it.

**Rotate (no downtime; the API accepts a current and a next secret).**
1. `.\scripts\New-ProductionKeys.ps1 -OutputDirectory <new folder> -ProxySecretOnly`; put the new value in the password manager and print it.
2. Render: add `Proxy__NextSecret` = the new value. Redeploy.
3. Pages: change `PROXY_SECRET` to the new value. Redeploy the site. Run the smoke test.
4. Render: set `Proxy__Secret` to the new value and delete `Proxy__NextSecret`. Redeploy.

**If it is lost.** Nothing is lost that cannot be remade: make a new one and follow the steps above, accepting a short outage (set `Proxy__Secret` and `PROXY_SECRET` together).

**What users notice.** If lost and replaced in a hurry: the site shows "The server is starting up" for the few minutes the two values differ.

## Other secrets that are simply replaced

The Turnstile secret, the R2 tokens, the database password and the backup keys can all be replaced without losing anything; see "Rotating keys" in `docs/deployment.md`.

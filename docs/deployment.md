# Putting Phase 1 online

Free tiers only: **browser → Cloudflare Pages** (the front end, and a Pages Function that sends `/api/*` on) **→ Render** (the API, in Docker, Frankfurt) **→ Neon** (a separate production branch). Audit anchors go to a **Cloudflare R2** bucket. Cloudflare **Turnstile** guards sign-in and sign-up.

This is a demonstration with made-up data: every page says so, and search engines are told to stay away.

Nothing here is done for you. The code and configuration are ready (`render.yaml`, `frontend/functions/api/[[path]].ts`, `frontend/public/_headers`, the scripts in `scripts/`); the steps below are the parts only you can do, with your own accounts. The accounts you need: Neon, Render, Cloudflare (Pages, R2 and Turnstile are all in one Cloudflare account), and GitHub (you have it). **Cloudflare asks for a payment method before it will switch R2 on**, even though this use stays inside R2's free allowance (10 GB stored, a million writes and ten million reads a month; check the figures on Cloudflare's pricing page). Anything beyond the free allowance would be charged, so set a billing alert there. Nothing else here needs a card.

## Read this first

- **The first request can take about a minute.** Render's free plan puts the API to sleep after 15 minutes without a request, and waking it takes up to a minute. Neon's free compute also sleeps and adds a second or two on the first query. The Pages Function waits up to 75 seconds for the API, so the first visitor sees a slow page, not an error. Run the smoke test (step 11) a minute before you show anyone.
- **Render's free plan has no shell.** Everything that touches the database from outside the running app (`migrate`, the grants, `seed-admin`, `verify-audit`) is run **from your own machine**, in a terminal, with the production values in that terminal's environment only (see "Running a command against production").
- **Production never changes its own schema.** The app does not migrate at startup. You run `migrate`, deliberately, before you deploy code that needs the change.
- **Only the proxy can reach the API.** The Pages Function adds a secret header to every request. The API refuses anything without it (except `/health/live`, which says "Healthy" and nothing more).

## What each secret is

Every secret is an environment variable. None is in `render.yaml` (it asks for each value when you first apply it) and none is in the repository. Generate the first group with `scripts/New-ProductionKeys.ps1`; the others come from the services themselves.

| Setting | Where it is set | What it is, and how to make it | If it is lost |
| --- | --- | --- | --- |
| `Audit__Keys__k1` | Render | The key that signs the audit chain: 32 random bytes, base64. `New-ProductionKeys.ps1` makes it. | **The audit chain can never be verified again.** Back it up before anything else. |
| `DataProtection__CertificateBase64`, `DataProtection__CertificatePassword` | Render | The certificate that encrypts the data protection keys kept in the database: a base64 PFX and its password. `New-ProductionKeys.ps1` makes both. | The stored keys cannot be read: every session ends and every enrolled authenticator becomes unreadable (everyone would have to set up two-factor again). |
| `Proxy__Secret` | Render | The secret the proxy sends: 64 random characters. `New-ProductionKeys.ps1` makes it. | Make a new one (see "Rotating the proxy secret"). |
| `PROXY_SECRET` | Cloudflare Pages (production, as a secret) | The same value as `Proxy__Secret`. | As above. |
| `ConnectionStrings__Default` | Render | The application's own login, `case_register_app`, on the production database: the direct Neon host (not `-pooler`), `SSL Mode=VerifyFull`, `Maximum Pool Size=10`. Step 1. | Reset the role's password in the SQL editor and set it again. |
| `ConnectionStrings__Migrations` | **Your machine only**, never Render | The database owner's direct connection string. Used by `migrate`, `seed-admin` and `verify-audit` from your terminal. | Reset the owner's password in the Neon console. |
| `Audit__R2__Endpoint`, `Audit__R2__Bucket`, `Audit__R2__AccessKeyId`, `Audit__R2__SecretAccessKey` | Render | The R2 bucket for the audit anchors and an API token that can reach that one bucket. Step 3. | Make a new token; the anchors in the bucket are not affected. |
| `Turnstile__SecretKey` | Render | Turnstile's secret key for the production site. Step 5. | Rotate it in the Turnstile dashboard. |
| `VITE_TURNSTILE_SITE_KEY` | Cloudflare Pages (production; read at build time) | Turnstile's **public** site key. Not a secret: it is built into the page. Never put a secret in a `VITE_` variable. | Copy it from the Turnstile dashboard. |
| `Deployment__Mode` | Render (already in `render.yaml`) | `Demo`, the only value allowed for now. The API refuses to start with anything else (or with nothing), and the pages show the demo notice because of it. It has no default on purpose. | Set it to `Demo`. |
| `Invites__SetupPageUrl` | Render | `https://<your Pages hostname>/staff/setup`. Where a staff invite link points. | Set it again. |
| `API_ORIGIN`, `PRODUCTION_HOSTNAME` | Cloudflare Pages (production) | The API's address (`https://<service>.onrender.com`) and the one hostname the site answers `/api` on. Not secrets. | Set them again. |
| `ASPNETCORE_ENVIRONMENT` | Render (already in `render.yaml`) | `Production`. | |

Production keys are made fresh. Never use the development ones.

**Back up the audit key and the certificate in your password manager before the first deploy.** Neither can be made again.

## 1. Neon: a production branch of its own

Production gets its own branch, with its own owner and its own application password, and nothing in dev ever touches it.

1. In the Neon console, create a **branch** called `production` (from your default branch). A branch starts as a copy of its parent, so it will contain your development database, `neondb`. You will not use that copy.
2. Open the SQL editor **on the `production` branch**, signed in as the owner (`neondb_owner`), and create the database the production app will use:

   ```sql
   CREATE DATABASE caseregister_prod;
   ```

3. Delete the inherited copy so no development data is sitting in production: in the console, branch `production` → Databases → delete `neondb`. (If you would rather keep it until you are sure, leave it, but nothing may point at it.)
4. Make a long random password for the application's login. This puts it in your clipboard without printing it:

   ```powershell
   $b = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
   $pw = ([BitConverter]::ToString($b) -replace '-', '').ToLower(); Set-Clipboard $pw
   ```

5. In the SQL editor, connected to **`caseregister_prod`** as the owner, create the login with plain SQL (paste the password where it says so). **Do not create it with the Neon console, CLI or API**: those roles join `neon_superuser`, which cannot be taken away (the grants script refuses such a role):

   ```sql
   CREATE ROLE case_register_app LOGIN PASSWORD '<paste the password>';
   ```

6. Make the two connection strings from the console's "Connection details" (choose the branch `production` and database `caseregister_prod`; use the **direct** host, the one **without** `-pooler`):

   - `ConnectionStrings__Default`: `Host=<host>;Database=caseregister_prod;Username=case_register_app;Password=<the password>;SSL Mode=VerifyFull;Maximum Pool Size=10;Timeout=30;Command Timeout=30`
   - `ConnectionStrings__Migrations`: the same host and database, `Username=neondb_owner` and the owner's password. This one stays on your machine.

   If the first deploy's log shows a certificate error for the database, change `VerifyFull` to `Require` and redeploy.
7. Put both strings, and the application password, in your password manager.

**Before every later migration, take a backup** (see "Changing the schema later").

## 2. Keys

On your machine, in PowerShell, from the repository folder:

```powershell
.\scripts\New-ProductionKeys.ps1 -OutputDirectory $HOME\caseregister-keys
```

It writes each secret to its own file in that folder (which must be outside any git repository), prints only the file names and which setting each belongs in, and tells you when the certificate expires. It prints no secret.

1. Copy every file into your password manager. **The audit key and the certificate cannot be made again.**
2. Write the certificate's **expiry date** (the script prints it; it is five years from the day you made it) in your calendar, two months early. Replacing it is supported (the old one is kept for reading; see `docs/runbooks/key-recovery.md`), but do not leave it to the last week.
3. Delete the folder when everything is in the password manager.

## 3. Cloudflare R2: the audit anchors

Anchors are copies of the audit chain's latest hash, kept in a bucket you control, so someone with only the database cannot rewrite history without it showing.

1. Cloudflare dashboard → **R2** → create a bucket, for example `caseregister-anchors`. Leave it private (no public access, no custom domain).
2. **Turn on a bucket lock**: bucket → Settings → **Bucket lock** → add a rule that keeps every object, for example prefix `anchors/` kept **indefinitely**. **This lock is what stops anchors being deleted or overwritten, and it is the only thing that does.** R2 has no write-only permission: any token that can add an object can also replace or delete it, so the application's token, if someone steals it, could erase anchors were the lock not there. (The application also writes each anchor under its own name and tells R2 not to replace an object that exists, but that protects against mistakes, not against a thief.) The exact wording on the page may differ; what you want is "no object can be deleted or changed".
3. **R2 → Manage API tokens → Create API token** for the application: permission **Object Read & Write**, scoped to **this bucket only** (not "all buckets"). Copy the **Access Key ID** and **Secret Access Key** (shown once) and the account's S3 address, which looks like `https://<account id>.r2.cloudflarestorage.com` (no bucket name, no path). These are `Audit__R2__AccessKeyId`, `Audit__R2__SecretAccessKey` and `Audit__R2__Endpoint` on Render; the bucket name is `Audit__R2__Bucket`.
4. **A second token for your own machine**: create another token, permission **Object Read only**, scoped to the same bucket. Use this one, and never the application's, when you run `verify-audit` or `verify-keys` from your terminal (it only reads anchors). Then a stolen laptop or a leaked terminal history cannot write to the bucket.
5. Put all of them in your password manager.

**When anchors are written (there is no scheduler):** after the database has committed, never inside it. An anchor of the chain as it then stands is requested by the first audit entry on each new Zambian day, by the first entry after an hour has passed since the last anchor, immediately after every confirmed proposal, and after every break-glass command. The upload is inline with a 3-second limit; there are no background tasks, because Render can stop the instance at any moment. If the bucket cannot be reached the action that caused it has already happened and goes ahead; an `AnchorDeferred` entry is written to the audit log (once, while the bucket stays down), and the next audit write tries again. You can write one by hand any time with `anchor-audit` (below). **Production refuses to start without the bucket settings, and refuses the local-file anchor store.**

`verify-audit` compares the database with **every** anchor in the bucket and reports the first divergence (the lowest sequence number that disagrees), whether it is a changed entry, a cut-off tail or an anchor that was altered.

## 4. Cloudflare Pages: the front end

1. Dashboard → **Workers & Pages → Create → Pages → Connect to Git** → this repository. Name the project (this decides your address: `<name>.pages.dev`; if the name is taken Cloudflare adds letters, so read the address it gives you).
2. Build settings:
   - **Production branch**: `main`
   - **Root directory**: `frontend`
   - **Build command**: `npm ci && npm run build`
   - **Build output directory**: `dist`
   - Variable **`NODE_VERSION`** = `24`
3. **Preview deployments: turn them off.** Settings → Builds → Branch control → **Preview deployments** → **None** (production branch only). Pull requests then never get a public copy. As a second lock, the Function answers `/api` on **one hostname only** (`PRODUCTION_HOSTNAME`) and the preview environment is never given the proxy secret, so even a preview that existed could not reach the production API. Leave the **Preview** environment's variables empty.
4. The first build will succeed without any other variable; sign-in will say the security check is not set up until step 7.

Write down the project's address (without `https://`): it is your **Pages hostname**, for example `caseregister.pages.dev`.

## 5. Turnstile

1. Dashboard → **Turnstile → Add widget**. **Hostname: your Pages hostname only** (this restricts the widget to the production site; it will refuse to run anywhere else). Widget mode: Managed.
2. Copy the **site key** (public) and the **secret key**. The site key is `VITE_TURNSTILE_SITE_KEY` (Pages); the secret key is `Turnstile__SecretKey` (Render).
3. The development test keys stay in development only (`frontend/.env.development` and `appsettings.Development.json`); production never uses them.

## 6. Render: the API

1. Render dashboard → **New → Blueprint** → connect this repository, branch `main`. Render reads `render.yaml`: a Docker web service called `caseregister-api` in **Frankfurt** on the **free** plan, health check `/health/live`, `ASPNETCORE_ENVIRONMENT=Production`, and **auto-deploy only after the CI checks pass**.
2. Render asks for each setting marked `sync: false`. Give it the values from your password manager (table above): `ConnectionStrings__Default`, `Audit__Keys__k1`, the four `Audit__R2__*`, the two `DataProtection__*`, `Turnstile__SecretKey`, `Proxy__Secret`, and `Invites__SetupPageUrl` = `https://<Pages hostname>/staff/setup`. **Do not** give it `ConnectionStrings__Migrations`.
3. Apply. The first build takes several minutes. The service gets an address like `https://caseregister-api.onrender.com` (read the real one off the service page): that is the **API origin**.
4. The service starts and answers `/health/live`, but nothing else works until the database has its tables (step 8).

If the service refuses to start, its log says which **setting** is missing or wrong, by name, never its value: a missing proxy secret, a missing R2 setting, a short audit key, and so on. Fix that one setting and redeploy.

## 7. Connect the two

In Pages → Settings → **Variables and secrets** → **Production**:

| Variable | Value | Type |
| --- | --- | --- |
| `API_ORIGIN` | the API origin from step 6, `https://…onrender.com` | text |
| `PRODUCTION_HOSTNAME` | your Pages hostname, `…pages.dev` | text |
| `PROXY_SECRET` | the same value as `Proxy__Secret` on Render | **secret** |
| `VITE_TURNSTILE_SITE_KEY` | the Turnstile site key | text |
| `NODE_VERSION` | `24` | text |

Then **redeploy** (Deployments → the latest → Retry, or push a commit) so the build picks up the site key. Nothing goes in the Preview environment.

The Function (`frontend/functions/api/[[path]].ts`) now sends `/api/*` to Render with the secret, forwards cookies and `Set-Cookie` untouched (so the `__Host-` cookies work), caps request bodies at 64 KiB, gives each call 75 seconds, strips every header it does not need (including any forwarding headers and any copy of the secret header a visitor sends), tells the API the visitor's address (from `CF-Connecting-IP`, which only Cloudflare sets) so rate limits and the audit log see the visitor and not the proxy, and never logs a body, a cookie or the secret.

## Running a command against production

`migrate`, `seed-admin`, `anchor-audit` and `verify-audit` run from your machine against the production database. Open a **new PowerShell window** for each session so nothing lingers, set the values for that window only, and close it afterwards. This function asks for a value without showing it:

```powershell
function Ask([string]$Name) {
  $secure = Read-Host $Name -AsSecureString
  $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
  try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}
$env:ASPNETCORE_ENVIRONMENT = 'Production'     # so no development secrets are read
```

What each command needs in its environment (set with `$env:Name = Ask 'Name'`):

| Command | Variables |
| --- | --- |
| `migrate` | `ConnectionStrings__Migrations` |
| `seed-admin`, `reset-two-factor`, `suspend-admin`, `anchor-audit`, `verify-audit`, `verify-keys` | `Deployment__Mode` (`Demo`), `ConnectionStrings__Default`, `ConnectionStrings__Migrations`, `Audit__Keys__k1`, `Audit__R2__Endpoint`, `Audit__R2__Bucket`, `Audit__R2__AccessKeyId`, `Audit__R2__SecretAccessKey`, `DataProtection__CertificateBase64`, `DataProtection__CertificatePassword` |

For `verify-audit` and `verify-keys` put the **read-only** R2 token's key id and secret in `Audit__R2__AccessKeyId` and `Audit__R2__SecretAccessKey` (step 3 of the R2 section); they only read anchors. `anchor-audit` and `seed-admin` and the break-glass commands write audit entries and, through them, anchors, so they need the application's token.

The variables are only in that window. Environment variables beat user-secrets, so the development values are never used while they are set, and each command prints the database host and name before it does anything, so you can see it is the production one.

Run a command like this (the `--no-launch-profile` matters: it stops the development profile from setting the environment to Development):

```powershell
dotnet run --project CaseRegister.Api --configuration Release --no-launch-profile -- migrate
```

## 8. Migrate

Render's app never migrates. From your machine:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:ConnectionStrings__Migrations = Ask 'Owner connection string (direct host, not -pooler)'
dotnet run --project CaseRegister.Api --configuration Release --no-launch-profile -- migrate
```

It shows the **database host and name**, lists the **pending migrations** in order, reminds you to take a backup, and waits for you to type `yes`. Anything else changes nothing. It refuses a `-pooler` host, a redirected input and any arguments. For a brand-new database there is nothing to back up; for every later migration there is (see "Changing the schema later").

## 9. Grants

The application's login may only do what it needs: no schema changes, an append-only audit log, no deletes on records that are never deleted. `database/app-role-grants.sql` sets this up, and it is safe to run again whenever you like.

Run it **as the owner**, after the migrations, against **`caseregister_prod`**: paste the whole file into the Neon SQL editor (branch `production`, database `caseregister_prod`), or from a machine with `psql`:

```powershell
psql "<owner connection string>" -f database\app-role-grants.sql
```

It stops with a clear message if `case_register_app` does not exist or is the wrong kind of role. **Run it again after every migration that adds a table.**

## 10. First admins

Approving anyone takes two admins, so the first two are created by hand, in an interactive terminal (the password is typed at a masked prompt):

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:ConnectionStrings__Default = Ask 'Default connection string (case_register_app)'
$env:ConnectionStrings__Migrations = Ask 'Owner connection string'
$env:Audit__Keys__k1 = Ask 'Audit key k1'
$env:Audit__R2__Endpoint = Ask 'R2 endpoint'; $env:Audit__R2__Bucket = Ask 'R2 bucket'
$env:Audit__R2__AccessKeyId = Ask 'R2 access key id'; $env:Audit__R2__SecretAccessKey = Ask 'R2 secret access key'
$env:DataProtection__CertificateBase64 = Ask 'Certificate (base64)'; $env:DataProtection__CertificatePassword = Ask 'Certificate password'
dotnet run --project CaseRegister.Api --configuration Release --no-launch-profile -- seed-admin
dotnet run --project CaseRegister.Api --configuration Release --no-launch-profile -- seed-admin
```

Each asks for an email, a full name and a password of at least 12 characters (checked against Have I Been Pwned), shows the database host it will write to, and waits for `yes`. Each admin sets up two-factor the first time they sign in. After two admins exist the command refuses: from then on admins are made through the approval screens. **`seed-demo` refuses to run in production**: there is no demo data here, and people apply through the real sign-up form.

## 11. Smoke test

```powershell
.\scripts\smoke-test.ps1 -SiteUrl https://<Pages hostname> -ApiUrl https://<API origin>
```

It prints PASS or FAIL for each check and exits 1 if any failed: the app is served; the strict security headers are on the pages (policy that allows no inline script and only Turnstile as an outside origin, HSTS, nosniff, referrer and permissions policies); built files are cached for a year and the page is not; search engines are kept out (`robots.txt`, the meta tag and the header); the API answers through the proxy with its own security headers; the `__Host-` cookie comes through intact; **a direct request to Render without the secret is refused**; Render answers `/health/live` and nothing else; and no `Server` header names the software behind it. (Cloudflare itself puts `Server: cloudflare` on responses; that is allowed.)

If it says there was no answer, wait a minute and run it again: the API may have been asleep.

Then do this short checklist by hand, because a script cannot sign in for you:

1. Sign in as the first admin; set up two-factor with your authenticator app; keep the recovery codes.
2. Sign in as the second admin (a private window) and do the same.
3. Apply as a test agent at **Apply to join** (made-up details).
4. As admin 1 propose the approval; as admin 2 confirm it with a fresh code.
5. Sign in as the agent; set up two-factor.
6. As an admin open **Audit log** and press **Verify chain**: it should say the chain is intact and that anchors were checked.
7. In the R2 bucket, look for an object under `anchors/` with today's date.
8. Look at the Render log: one JSON line per request, no errors, and no emails, passwords, codes or tokens in it.

## 12. Tag the release

After step 11 passes and the checklist is done, mark the version that is live:

```powershell
git switch main; git pull
git tag -a v0.1.0 -m "Phase 1: case register, sign-in with two-factor, maker-checker approvals, hash-chained audit log"
git push origin v0.1.0
```

Then write short release notes (GitHub → Releases → Draft a new release → choose `v0.1.0`), or `gh release create v0.1.0 --title "v0.1.0" --notes-file notes.md`. Something like:

> **v0.1.0: Phase 1 demonstration**
> Members' sign-up and sign-in with mandatory two-factor; admin approvals that need two different admins; staff invites; practising certificates; a hash-chained audit log with daily anchors in R2. Deployed on free tiers (Cloudflare Pages, Render, Neon). All data is made up. Known limits: the API sleeps after 15 idle minutes (the first request takes about a minute); one data protection certificate; no agency compliance screens yet.

## Changing the schema later

Migrations are applied by you, before the code that needs them is deployed, and the old version of the app is still serving while the new one starts (Render keeps the previous deploy running until the new one is healthy), and a rollback puts it back. So **every migration must keep the previous version of the app working**:

- **Add first, remove later.** One release adds a column, table or index (nullable, or with a default). A later release stops using the old thing. A third drops it. Never rename or drop something in the same release that stops using it.
- A new column the old code does not know about must not break the old code's inserts: make it nullable or give it a default.
- Do not change a column's type or tighten a constraint in a way old rows or old code can violate, in one step.
- Anything that rewrites a big table, or takes a long lock, waits for a quiet moment.

The order for a release that has a migration (because merging to `main` deploys by itself once CI passes):

1. **Back up.** In Neon, create a branch of `production` named for today (`backup-2026-11-03`): it is an instant, separate copy that stays until you delete it. (Neon also keeps a short history you can restore from; check how far back your plan goes. For an offline copy as well: `docker run --rm postgres:17 pg_dump "<owner connection string>" > backup.sql`.) This is your **restore point**.
2. `migrate` (step 8): read the list, type `yes`.
3. Run the grants again (step 9) if a table was added.
4. Merge the pull request. Render deploys once the CI checks pass.
5. Smoke test. Delete old backup branches when you are sure.

## Rolling back

- **The API:** Render → the service → Events (or Deploys) → pick a previous successful deploy → **Rollback**. This does not undo a migration, which is why migrations are add-only.
- **The front end:** Cloudflare Pages → the project → Deployments → a previous production deployment → **Rollback to this deployment**.
- **The database:** restore from the backup branch (or Neon's history) only for a migration that went wrong. Do not do it casually: anything written since then is lost, and the audit chain will say so (verify it afterwards).

## Rotating keys

- **The proxy secret** (no downtime, because the API accepts a current and a next secret):
  1. `.\scripts\New-ProductionKeys.ps1 -OutputDirectory <new folder> -ProxySecretOnly`.
  2. Render: add `Proxy__NextSecret` = the new value. Redeploy. The API now accepts both.
  3. Pages: change `PROXY_SECRET` to the new value. Redeploy the site. Check the smoke test.
  4. Render: set `Proxy__Secret` to the new value and delete `Proxy__NextSecret`. Redeploy.
  5. Update your password manager.
- **The audit key, the pseudonymisation key and the data protection certificate:** see `docs/runbooks/key-recovery.md`, which also says where each is escrowed and what happens if one is lost. In short: the application keeps **every** audit key you have ever configured (only the current one signs), so a rotation adds a key and never removes one; an older data protection certificate is kept as `DataProtection__Previous__<n>__...` so the keys it protected stay readable; and `verify-keys` checks it all.
- **Turnstile secret:** rotate in the Turnstile dashboard, then set `Turnstile__SecretKey` on Render.
- **The R2 token:** create a new token for the bucket, set the three `Audit__R2__*` values on Render, check the next audit write succeeds, then delete the old token.
- **The application's database password:** `ALTER ROLE case_register_app PASSWORD '<new>';` in the SQL editor, then update `ConnectionStrings__Default` on Render.

## Checking the audit chain in production

From your machine (the same environment as `seed-admin`, step 10):

```powershell
dotnet run --project CaseRegister.Api --configuration Release --no-launch-profile -- verify-audit
dotnet run --project CaseRegister.Api --configuration Release --no-launch-profile -- verify-audit --from-anchor
```

It prints how many entries it checked and how many anchors it looked at, and either "The audit chain is intact." or where it is broken. The same check is the **Verify chain** button on the audit page. To write an anchor by hand: `-- anchor-audit`.

## Break-glass commands

For when the normal two-admin process cannot work. Both run from your machine in the same environment as `seed-admin`, ask for everything at the keyboard (nothing on the command line), print the database host and name first, and need the person's email typed twice and a reason of at least 30 characters.

```powershell
dotnet run --project CaseRegister.Api --configuration Release --no-launch-profile -- reset-two-factor
dotnet run --project CaseRegister.Api --configuration Release --no-launch-profile -- suspend-admin
```

- **`reset-two-factor`** is for a lost phone when no second admin can confirm (for example an admin, with only two admins). It removes the person's authenticator and recovery codes, signs them out everywhere and prints a one-time enrolment code, **once**. Give it to the person in person or by phone; they need it, with their password, at their next sign-in. It lasts 72 hours and is used up when they finish setting up two-factor; if they abandon set-up they can come back with the same code. Wrong tries count towards the ten-wrong-codes limit.
- **`suspend-admin`** suspends an admin even if that leaves fewer than two active admins. It is the only way below the floor of two.
- Both refuse if the audit key configured here is not the one that signed the latest audit entry (the wrong keys are in this window), if the migrations are behind, or if a break-glass action was already run on that person in the last 24 hours.
- Both write the audit log (`BreakGlassUsed`, with the reason, and the ordinary entry for what was done) and send a **critical** notification to every admin and to the person, which each has to acknowledge.

After a `suspend-admin` that leaves fewer than two admins, use `seed-admin` (it works while fewer than two active admins exist) or the normal approval process to add more.

## Free-tier limits worth knowing

| Service | Limit that matters here |
| --- | --- |
| Render (web, free) | Sleeps after 15 minutes without a request, and takes up to about a minute to wake. About 750 free instance hours a month, which covers one always-on-ish service. 512 MB of memory. |
| Neon (free) | Compute sleeps when idle; the first query after is a second or two slower. Storage and compute-hour limits are small but far above this use. |
| Cloudflare Pages (free) | A limited number of builds a month and 100,000 Function requests a day. Only `/api/*` runs the Function (`_routes.json`), so pages and files do not count. |
| Cloudflare R2 (free allowance) | 10 GB stored, a million writes and ten million reads a month. One small anchor a day is nothing. Needs a payment method on file. |
| Turnstile | Free. |

## If something goes wrong

| What you see | What to check |
| --- | --- |
| The site loads but sign-in says the security check is not set up | `VITE_TURNSTILE_SITE_KEY` is missing from the Pages **production** variables, or the site was not rebuilt after it was added. |
| `/api` answers 503 "This site is not available" | `API_ORIGIN`, `PROXY_SECRET` (at least 32 characters) or `PRODUCTION_HOSTNAME` is missing or wrong in Pages (production). |
| `/api` answers 404 on the site | You are on a hostname other than `PRODUCTION_HOSTNAME` (for example a preview address). |
| `/api` answers 403 | `PROXY_SECRET` on Pages does not match `Proxy__Secret` (or `Proxy__NextSecret`) on Render. |
| `/api` answers 504 | The API was asleep and took longer than 75 seconds; try again. |
| Render will not start | Read the log: the message names the missing or wrong setting, never its value. |
| Everyone is signed out after a deploy | The data protection certificate or its password changed. Put the original back. |
| **Verify chain** says an anchor is unreadable | R2 could not be reached, or the token is wrong. Anchors already in the bucket are safe. |

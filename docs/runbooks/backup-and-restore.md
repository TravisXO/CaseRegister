# Backups and restore

Neon keeps its own short history, but that history lives with the same provider and the same account as the database. This is a second, separate copy: every night a full dump of the database, **encrypted so that only a private key kept offline can open it**, goes into its own R2 bucket and stays for 35 days. Once a month a second workflow proves the newest dump can really be restored and that the audit chain on the restored copy verifies.

| Workflow | When | What it does |
| --- | --- | --- |
| `.github/workflows/backup.yml` | every day at 02:00 UTC | `pg_dump --format=custom` of the production database as a read-only role, encrypted with `age`, uploaded to the backup bucket |
| `.github/workflows/restore-check.yml` | the 1st of every month at 04:00 UTC | decrypts the newest dump, restores it into a throwaway PostgreSQL container, runs `verify-audit --restored-backup` on it. **The job fails if either step fails** |

Both can also be started by hand (Actions tab → the workflow → **Run workflow**). **Until the secrets below exist, both skip with a notice** ("Backup skipped: … Missing: …") and the run is green, so adding the workflows breaks nothing.

Cost: nothing beyond what you already have. GitHub Actions minutes for a public repository are free, and a nightly dump of a demo-sized database is a few megabytes in an R2 bucket that stays well inside the free 10 GB.

## 1. A read-only database role for the backup

The backup needs to read everything and change nothing, so it does not use the application's role (which can write) or the owner's. In the Neon SQL editor, as the owner of the production database (replace the password, and use a long random one):

```sql
CREATE ROLE backup_reader WITH LOGIN PASSWORD 'a-long-random-password';
GRANT CONNECT ON DATABASE neondb TO backup_reader;      -- use your database's name
GRANT USAGE ON SCHEMA public TO backup_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO backup_reader;
GRANT SELECT ON ALL SEQUENCES IN SCHEMA public TO backup_reader;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO backup_reader;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON SEQUENCES TO backup_reader;
```

The last two lines make tables and sequences added by later migrations readable too. They apply to objects created by the role that runs them, so run them as the same owner role that runs `migrate`. If a later dump complains about a table it cannot read, run the two `GRANT … ALL` lines again.

The connection string for the workflow is the Neon connection string with this role and password, `?sslmode=require` at the end: `postgresql://backup_reader:…@<host>/<database>?sslmode=require`. Use the **direct** (not pooled) host; `pg_dump` does not work through a pooler.

## 2. Make the age key pairs

[age](https://age-encryption.org) is a small file-encryption tool. A key pair is a public key (`age1…`), which can only lock a file, and a private key (`AGE-SECRET-KEY-1…`), which opens it. The public key goes in the repository; the private key never does. Anyone who reads the repository, or steals the bucket's contents, can lock files but cannot read a single dump.

Do this on your own computer, once (install age from the [releases page](https://github.com/FiloSottile/age/releases), or `winget install FiloSottile.age`):

```bash
age-keygen -o recovery-key.txt        # the key that opens backups when disaster strikes
age-keygen -o restore-check-key.txt   # the key the monthly restore check uses (see the note below)
```

Each command prints `Public key: age1…`. The private key is the `AGE-SECRET-KEY-1…` line inside the file.

1. **recovery-key.txt**: move it somewhere offline (a USB stick in a drawer, a printed copy in a safe), **not** on the computer you use every day and **never** into GitHub. Without it the backups cannot be opened, by you or anyone else. A second copy in a second place is wise.
2. Put **both public keys**, one per line, in [`backup/age-recipients.txt`](../../backup/age-recipients.txt) (under the comment lines), commit and push. Every dump is then encrypted to both.
3. **restore-check-key.txt**: its private line goes into the GitHub secret `BACKUP_AGE_RESTORE_KEY` (below), then delete the file.

**Why a second key?** The monthly check has to open a dump, and a GitHub workflow cannot reach a key kept offline. So it has its own key, which can open the same dumps. The trade-off is plain: anyone who can read this repository's secrets *and* the backup bucket could open dumps. The recovery key itself stays offline, and the restore-check key can be dropped at any time by deleting its line from `age-recipients.txt` (later dumps are then locked to the recovery key only; the monthly check will then report that it cannot decrypt, which is your cue to run it by hand with the recovery key, see "Restoring for real"). If you would rather have no key in GitHub at all, leave out `BACKUP_AGE_RESTORE_KEY`: the monthly check will skip, and you must restore a dump by hand now and then.

## 3. The backup bucket

Cloudflare dashboard → **R2 → Create bucket**, for example `caseregister-backups`. It must be **a different bucket** from the audit anchors' (that one is locked forever; this one must expire).

1. Leave it private.
2. **Settings → Object lifecycle rules → Add rule**: prefix `dumps/`, **delete objects 35 days after they were uploaded**. Thirty-five days leaves a week's room over a month, so a problem noticed late can still be restored from.
3. **R2 → Manage API tokens → Create API token**, permission **Object Read & Write**, scoped to **this bucket only**. This is the token the nightly backup uses.
4. A second token for the restore check: permission **Object Read only**, this bucket only.

## 4. The GitHub secrets

Repository → **Settings → Secrets and variables → Actions → New repository secret**:

| Secret | Value | Used by |
| --- | --- | --- |
| `BACKUP_DATABASE_URL` | the `backup_reader` connection string from step 1 | backup |
| `BACKUP_R2_ENDPOINT` | `https://<account id>.r2.cloudflarestorage.com` | both |
| `BACKUP_R2_BUCKET` | the backup bucket's name | both |
| `BACKUP_R2_ACCESS_KEY_ID`, `BACKUP_R2_SECRET_ACCESS_KEY` | the Object Read & Write token | backup |
| `BACKUP_R2_READ_ACCESS_KEY_ID`, `BACKUP_R2_READ_SECRET_ACCESS_KEY` | the read-only token | restore check |
| `BACKUP_AGE_RESTORE_KEY` | the private line (`AGE-SECRET-KEY-1…`) of `restore-check-key.txt` | restore check |
| `RESTORE_CHECK_SETTINGS` | the settings below | restore check |

`RESTORE_CHECK_SETTINGS` is one secret holding `NAME=value` lines, because `verify-audit` needs the same keys the application signs with, and rotation (see [key-recovery.md](key-recovery.md)) can add more than one:

```text
Deployment__Mode=Demo
Audit__Keys__k1=<every audit key ever used, one line each: Audit__Keys__k1=..., Audit__Keys__k2=...>
Audit__CurrentKeyId=<the current key's id>
Pseudonym__Keys__p1=<the pseudonym key>
Pseudonym__CurrentKeyId=<its id>
Audit__R2__Bucket=<the bucket of the AUDIT ANCHORS (not the backup bucket)>
DataProtection__CertificateBase64=<as on Render>
DataProtection__CertificatePassword=<as on Render>
```

Add a line here whenever you rotate a key, in the same hour you rotate it on Render; a monthly check that cannot find a key reports "UnknownKey" and fails, which is the reminder you forgot. These values are the application's own secrets, so the same care applies: they are masked in the workflow log, and only people who can already change the repository's secrets can reach them. The database and the anchors token are not in the list on purpose: the workflow points the check at the throwaway database and gives it a **read-only** token, whatever this secret says.

Run **backup** from the Actions tab once, by hand. A green run that says "Uploaded dumps/caseregister-….dump.age" means it works; check the object is in the bucket. Then run **Backup restore check** by hand and read the log: it should end with "The audit chain is intact" and say how many anchors are newer than the copy (all anchors made since the dump was taken; those cannot be compared and are counted, not ignored silently).

## 5. Keep it running

**GitHub turns scheduled workflows off in a public repository after 60 days with no repository activity** (no commits, issues, pull requests or similar), and sends only an email to the person who last changed the schedule. A quiet demo project can fall asleep this way without anyone noticing. So:

- Look at the bucket now and then: there should be a new `dumps/…` object every day.
- If they have stopped, open the repository's **Actions** tab, choose the workflow and press **Enable workflow**; then run it once by hand.
- Any commit to the repository restarts the 60-day clock.

## Restoring for real

1. Download the dump you want from the backup bucket (R2 dashboard, or `aws s3 cp --endpoint-url …`).
2. Decrypt it with the recovery key: `age --decrypt --identity recovery-key.txt --output backup.dump backup.dump.age`.
3. Create an empty PostgreSQL 18 database and restore: `pg_restore --no-owner --no-privileges --dbname <connection string> backup.dump`.
4. Run the migrations and the grants script (`database/app-role-grants.sql`) as the owner, so the application's role has exactly the access it had.
5. Check it before anyone uses it: `verify-audit --restored-backup` (this is the same command the monthly check runs) and `verify-keys`. Everything entered after the dump was taken is lost; the audit anchors newer than the dump are the record of how far the chain had got.

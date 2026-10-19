# Trying the whole Phase 1 flow by hand

This walks through the flow in a browser, from an empty database: two admins, an agent who applies, both admins approving them, and the agent signing in. All data is made up, and no email is sent anywhere (so the addresses only have to look like addresses; `example.test` is reserved and never belongs to a real person).

## What you need

- the .NET 10 SDK, Node 24 and npm
- an authenticator app on your phone (any app that makes 6-digit codes)
- the API's user-secrets already set (the database, the data-protection certificate and the audit key; see the notes in `CaseRegister.Api` and `database/app-role-grants.sql`) and, since the audit log stopped storing plain hashes of email addresses, a pseudonymisation key:
  ```powershell
  dotnet user-secrets set "Pseudonym:Keys:p1" ([Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))) --project CaseRegister.Api
  ```
  (`Pseudonym:CurrentKeyId` is `p1` already. The API will not start without the key.)

## 1. An empty database

Use a Neon branch (or database) that has nothing in it: reset the development branch in the Neon console, or make a new branch and point the connection strings at it.

The API applies the migrations itself when it starts in Development, but the application role gets its permissions from a script, so the order matters:

1. Start the API once, so it creates the tables, then stop it with Ctrl+C:

   ```bash
   dotnet run --project CaseRegister.Api --launch-profile https
   ```

2. Give the application role its permissions. Run `database/app-role-grants.sql` as the database owner (in the Neon SQL editor, or with `psql`). The role `case_register_app` has to exist first; the top of that file says how to create it.

## 2. Two admins

Admins are created by hand, in a real terminal (the password is typed at a masked prompt, so a script or an IDE console will not do). Run it twice, once for each admin. It refuses a third.

```bash
dotnet run --project CaseRegister.Api --launch-profile https -- seed-admin
```

Give it an email (for example `admin1@example.test`, then `admin2@example.test`), a full name and a password of 12 or more characters. Neither admin has an authenticator yet: that is set up the first time they sign in.

## 3. Some fake applications to look at (optional)

```bash
dotnet run --project CaseRegister.Api --launch-profile https -- seed-demo
```

This makes four fake agencies and five pending applications (one names an agency the way an existing one is spelt, one has a flag to read, one asks for a new agency). It only works in Development, and running it again adds nothing.

## 4. Start both apps

In one terminal:

```bash
dotnet run --project CaseRegister.Api --launch-profile https
```

In another:

```bash
cd frontend
npm ci
npm run cert     # once: exports the ASP.NET Core development certificate for Vite
npm run dev
```

If the browser warns about the certificate, run `dotnet dev-certs https --trust` once and restart the browser. Open **https://localhost:5173**. The browser only ever talks to that address: Vite hands `/api` on to the API, the way the Cloudflare proxy will in production.

## 5. Admin 1: sign in and set up two-factor

1. Sign in as `admin1@example.test`. (The security check passes by itself in development.)
2. You are taken to **Set up two-factor sign-in**. Press **Start set-up**, scan the QR code with your authenticator app, and type the 6-digit code it shows.
3. The **recovery codes** appear, once. Copy or download them, tick the box, and carry on. (Leaving the page first asks you whether you are sure.)
4. You are on your profile, and the menu now has **Applications**, **Proposals**, **Invite staff**, **Account actions** and **Audit log**.

## 6. Admin 2, in a private window

Open a **private (incognito) window** and go to https://localhost:5173. Admin 1 and admin 2 cannot share one browser window: the sign-in cookie is shared by every tab in it, so signing in as the second admin would sign out the first.

Sign in as `admin2@example.test` and set up two-factor the same way. You need a second entry in your authenticator app.

## 7. An agent applies

Sign out (or use a third window), go to **Apply to join**, and fill in the form. For example:

| Field | Example |
| --- | --- |
| Full name | Chanda Banda |
| Email | `chanda@example.test` |
| Password | a phrase of 12 or more characters |
| Membership number | `3001` (with or without the prefix) |
| Class | Full |
| Agency | `Kafue Estates` (or the name of one of the demo agencies) |
| Certificate number | `9777/2026` |
| Certificate year | the current year |

You always get the same answer ("if your details are valid, your application has been submitted"): the form never says whether an email is already in use.

## 8. Approving takes both admins

1. **Admin 1**: **Applications** → find Chanda → **Approve…**. Choose the agency (or a new one), tick any flags after reading them, give a reason of 10 to 500 characters, and press **Propose approval**. Nothing has happened yet.
2. **Admin 2** (private window): **Proposals** → **Open** the proposal. It shows who proposed it, what it does, the reason and the flags. Type a **fresh** code from your authenticator and press **Confirm**.

   A code works once. If you have just used one (signing in, say), wait for the app to show the next one.
3. Admin 1 cannot confirm their own proposal: the page offers them only **Withdraw**.

## 9. The agent signs in

Sign out of the window you used for the agent. Sign in as `chanda@example.test`. You are asked to set up two-factor (same as the admins), and then you are on the profile: your agency, your roles and your certificate, which approval verified. The menu has no admin pages, and typing `/admin/applications` into the address bar shows "You do not have access to this".

## 10. The rest

- **Invite staff** (admin 1): propose an invitation. Admin 2 confirms it and is shown the one-time setup link, once. Open the link in a third window to set a password and two-factor as the new member of staff.
- **Account actions**: suspend a demo account (use the account ID shown on the application, in the proposal, or in the audit log). It asks "Are you sure?" first.
- **My sessions**: end another session; sign in from a second window to see one.
- **Audit log**: see every step above. **Verify chain** (admins only) checks that nothing was changed.
- **A certificate that has lapsed**: on the profile add a certificate for a past year and look at the banner.

## Tips

- **Rate limits reset when the API restarts.** If you see "Too many attempts. Try again in …", restart the API instead of waiting.
- **A two-factor code works once.** If one is refused straight after you used it, wait for the next.
- **Sessions end after 30 minutes without use**, and after 8 hours whatever you do. The app takes you back to sign-in and says so. The app never polls the API, so leaving a page open does not keep a session alive.
- **Times are Zambian time** (UTC+2), wherever your computer is.
- **Starting again:** reset the Neon branch and repeat from step 1.
- **The staff setup link** points at `https://localhost:5173/staff/setup` (set in `appsettings.Development.json`). If you run the front end on another address, set `Invites:SetupPageUrl` in user-secrets to match.
- **The API's own page** is at https://localhost:7205/scalar/v1 (Development only).

# Manual accessibility test

The automated checks (axe in every screen test, axe in a real browser over every main screen at a desktop width and at 320px, and Lighthouse on a phone's settings) catch a lot, but not what it is like to *use* the screens. This is a short list for a person to go through before showing the site to anyone, and after any change to how a screen looks or behaves. Allow about an hour. Write down what you find against the step number.

Use the **deployed site** or `npm run dev`. Sign in as a member and as an admin for the screens that need them (the demo accounts are in [manual-testing.md](manual-testing.md)). Turnstile on the sign-in and sign-up forms is Cloudflare's, so it matters that you try it with the real thing (step 9).

For the automated checks, see [testing.md](testing.md) ("Accessibility and size checks").

## 1. Keyboard only

Put the mouse out of reach. On every main screen (sign in, sign up, two-factor, profile, sessions, notifications, and the admin screens):

- [ ] **Tab** reaches every link, button and field, in an order that matches what you see. Shift+Tab goes back.
- [ ] You can always **see where you are**: a clear outline on whatever has focus, never lost behind the header or a message.
- [ ] Every button works with **Enter** or **Space**. Nothing needs a mouse (a hover, a drag, a right-click).
- [ ] After a **failed submit** the list of problems ("There is a problem") has focus and is read out; **Enter** on an item moves to its field.
- [ ] A table that is wider than the screen (the audit log, your passkeys) can be **focused and scrolled sideways with the arrow keys**.
- [ ] No keyboard trap: you can always Tab out of a dialog's content, and **Escape** closes the session-timeout dialog where it is allowed to close.

## 2. Screen reader on a computer (NVDA, with Firefox or Chrome)

NVDA is free (nvaccess.org). Turn on speech and listen; do not look at the screen for the first pass.

- [ ] The page **title and the main heading** say where you are. Landmarks list: the notices, banner, navigation, main, footer (NVDA+F7, or D to jump between landmarks).
- [ ] **Form fields** are read with their label, any hint, and "invalid" plus the reason when they have a problem.
- [ ] **Tables** are read with a caption and column headers; moving between cells says which column you are in.
- [ ] **Buttons that only say "Rename" or "Remove"** are read with what they are about ("Rename Work laptop").
- [ ] The **bell** says how many notifications are unread, and acting on one is announced once.
- [ ] **Setup key and recovery codes** (two-factor set-up): the key is read as groups of four characters; **Copy** says afterwards that it was copied; the recovery codes are a list you can read line by line.
- [ ] A **passkey** being added or used: the browser's own prompt is announced; the page says what happened in words afterwards.

## 3. Screen reader on a phone (TalkBack on Android, or VoiceOver on iPhone)

- [ ] Swipe through sign in: every field and button is reached, in order, with its label.
- [ ] The **two-factor code box** offers the number keypad, and the code from the authenticator app can be pasted (or offered by the keyboard as a suggestion).
- [ ] Targets are easy to hit with a finger: nothing is so small or so close to another that you touch the wrong one.
- [ ] **Sign in with a passkey** opens the phone's own prompt (fingerprint or face) and signs in.

## 4. Zoom to 400% and a 320px width

- [ ] In the browser, zoom to **400%** on a 1280px-wide window (this is the same as a 320px-wide screen), or use a phone in portrait with the browser's text size at its largest.
- [ ] Nothing is cut off, nothing overlaps, and **the page never scrolls sideways**. (Wide tables scroll inside their own box.)
- [ ] Every button and link is still reachable and its label is complete.
- [ ] Set the browser's **text size to Large/Very large** (not zoom): the layout still works.

## 5. Slow 4G with a slower processor

In Chrome: DevTools, Network tab, **Slow 4G**; Performance tab, gear icon, **CPU 4x slowdown**. Reload the sign-in page with the cache turned off (Disable cache).

- [ ] The page is usable within a few seconds, and does not jump about as it loads.
- [ ] While the API is waking up (Render's free tier sleeps), the page says "The server is starting up" in a status message, with a Retry button, and does not just show a blank page or a bare error.
- [ ] Nothing important is only shown while loading and then lost.

## 6. The session-timeout dialog

The warning appears shortly before the 30 minutes without activity end, and before the 8-hour limit. To see it sooner, ask for a short idle time in a local build, or wait.

- [ ] The dialog **takes focus** and a screen reader announces it; the first thing it reads is that the session is about to end and how long is left.
- [ ] **Stay signed in** works with the keyboard; the focus goes back to where it was.
- [ ] If you do nothing, the session ends and sign-in says so in words ("Your session ended") with focus at the top of the page.

## 7. Colour and contrast

- [ ] Turn on **Windows High Contrast** (or forced colours in the browser): text, links, buttons, focus outlines and errors are all still visible.
- [ ] Look at the screens in grey (an extension, or the OS colour filter): nothing relies on colour alone. Errors are in words, with an icon-free message, and links are underlined.

## 8. Reduced motion and print

- [ ] With "reduce motion" on in the system settings, nothing animates.
- [ ] On the recovery-codes screen, **Print** prints only the codes and the heading, with no menu, buttons or footer.

## 9. Turnstile (the security check) with a screen reader

This is Cloudflare's own widget, so test it on the deployed site, not on a local build. With NVDA or TalkBack:

- [ ] On sign in and sign up, the check is announced and can be reached by keyboard, and it finishes without you having to solve a picture puzzle (Managed mode normally passes with no interaction).
- [ ] If it **fails or cannot load** (turn off the network for the Cloudflare address, or block it with an extension), a message says so in words and **Try the security check again** is offered. After that, the sign-in works.
- [ ] The **two-factor code step has no Turnstile** (it follows the password step, which already passed).

## Reporting

For each problem: the screen, what you did, what you expected, what happened, the browser or assistive technology and its version. Fix in the code, then add a test (the vitest accessibility check on that screen, or an end-to-end check) so it cannot come back.

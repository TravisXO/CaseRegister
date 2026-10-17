import { useCallback, useEffect, useRef, useState } from "react";
import { onApiSuccess } from "../api/client";
import { api } from "../api/endpoints";
import { useSession } from "./session";

/** How long before the idle limit the warning appears. */
export const IDLE_WARNING_MS = 2 * 60 * 1000;
/** How long before the 8-hour limit the explanation appears. */
export const LIMIT_WARNING_MS = 5 * 60 * 1000;
const DEFAULT_IDLE_MS = 30 * 60 * 1000;

type Warning = "idle" | "limit" | null;

/**
 * The warning before a session ends (WCAG 2.2.1, timing adjustable). This file holds the only timers in the application. A timer restarts
 * with every successful API response, so a person who is working is never warned, and two minutes before the 30-minute idle limit an
 * accessible dialog offers to carry on. Five minutes before the 8-hour limit another explains that it cannot be extended. The timers
 * never call the API: "Stay signed in" does, when the person chooses it, with the cheapest authenticated request there is. At the limit
 * the page signs itself out of what it holds (the server has already ended the session by then).
 */
export function SessionTimeout() {
  const { state, signOut, endLocally } = useSession();
  const [warning, setWarning] = useState<Warning>(null);
  const [pending, setPending] = useState(false);
  const idleTimers = useRef<number[]>([]);
  const dialog = useRef<HTMLDivElement>(null);
  const opener = useRef<Element | null>(null);

  const ready = state.status === "ready";
  const idleMs = ready && state.session.idleTimeoutSeconds > 0 ? state.session.idleTimeoutSeconds * 1000 : DEFAULT_IDLE_MS;
  const endsAt = ready ? (state.session.sessionEndsAt ?? null) : null;

  const armIdle = useCallback(() => {
    for (const timer of idleTimers.current) {
      window.clearTimeout(timer);
    }

    idleTimers.current = [
      window.setTimeout(() => {
        setWarning((current) => current ?? "idle");
      }, Math.max(idleMs - IDLE_WARNING_MS, 0)),
      window.setTimeout(() => {
        endLocally();
      }, idleMs),
    ];
  }, [idleMs, endLocally]);

  // Any successful answer from the API means the person is active and the server has restarted its idle clock.
  useEffect(() => {
    if (!ready) {
      return undefined;
    }

    armIdle();
    onApiSuccess(() => {
      setWarning((current) => (current === "idle" ? null : current));
      armIdle();
    });

    return () => {
      onApiSuccess(null);
      for (const timer of idleTimers.current) {
        window.clearTimeout(timer);
      }

      idleTimers.current = [];
    };
  }, [ready, armIdle]);

  // The hard limit: explained five minutes before, and the page lets go of the session when it arrives.
  useEffect(() => {
    if (endsAt === null) {
      return undefined;
    }

    const remaining = new Date(endsAt).getTime() - Date.now();
    const timers = [
      window.setTimeout(() => {
        setWarning("limit");
      }, Math.max(remaining - LIMIT_WARNING_MS, 0)),
      window.setTimeout(() => {
        endLocally();
      }, Math.max(remaining, 0)),
    ];

    return () => {
      for (const timer of timers) {
        window.clearTimeout(timer);
      }
    };
  }, [endsAt, endLocally]);

  // Focus moves into the dialog when it opens and goes back to where it was when it closes.
  useEffect(() => {
    if (warning === null) {
      return undefined;
    }

    opener.current = document.activeElement;
    dialog.current?.querySelector<HTMLElement>("button")?.focus();

    return () => {
      if (opener.current instanceof HTMLElement && opener.current.isConnected) {
        opener.current.focus();
      }
    };
  }, [warning]);

  const stay = async () => {
    setPending(true);
    try {
      await api.session(); // a request that needs a session; its success restarts the clocks
    } catch {
      // A 401 has already sent the person back to sign-in; anything else leaves the warning up so they can try again.
    } finally {
      setPending(false);
    }
  };

  // Escape closes the warning (it never signs anyone out, and does not carry the session on either), and Tab stays inside the dialog.
  useEffect(() => {
    if (warning === null) {
      return undefined;
    }

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.stopPropagation();
        setWarning(null);

        return;
      }

      if (event.key === "Tab") {
        const buttons = [...(dialog.current?.querySelectorAll<HTMLElement>("button:not([disabled])") ?? [])];
        const first = buttons[0];
        const last = buttons[buttons.length - 1];
        if (first === undefined || last === undefined) {
          return;
        }

        if (!dialog.current?.contains(document.activeElement)) {
          event.preventDefault();
          first.focus();
        } else if (event.shiftKey && document.activeElement === first) {
          event.preventDefault();
          last.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
          event.preventDefault();
          first.focus();
        }
      }
    };

    document.addEventListener("keydown", onKeyDown);

    return () => {
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [warning]);

  if (!ready || warning === null) {
    return null;
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/60 p-4">
      <div ref={dialog} role="alertdialog" aria-modal="true" aria-labelledby="timeout-title" aria-describedby="timeout-body" className="card w-full max-w-md bg-white">
        {warning === "idle" ? (
          <>
            <h2 id="timeout-title" className="subheading mt-0">
              You are about to be signed out
            </h2>
            <p id="timeout-body" className="mb-4">
              You have not done anything for a while. In about 2 minutes you will be signed out to keep your account safe, unless you stay signed in.
            </p>
            <div className="flex flex-wrap gap-2">
              <button
                type="button"
                className="btn-primary"
                disabled={pending}
                onClick={() => {
                  void stay();
                }}
              >
                Stay signed in
              </button>
              <button
                type="button"
                className="btn-secondary"
                onClick={() => {
                  void signOut();
                }}
              >
                Sign out
              </button>
            </div>
          </>
        ) : (
          <>
            <h2 id="timeout-title" className="subheading mt-0">
              Your session is nearly at its time limit
            </h2>
            <p id="timeout-body" className="mb-4">
              However active you are, a session ends 8 hours after you signed in, and this cannot be extended. In about 5 minutes you will be signed out. Finish what you are doing and save anything you need; you can sign in again straight away.
            </p>
            <div className="flex flex-wrap gap-2">
              <button
                type="button"
                className="btn-primary"
                onClick={() => {
                  setWarning(null);
                }}
              >
                I understand
              </button>
              <button
                type="button"
                className="btn-secondary"
                onClick={() => {
                  void signOut();
                }}
              >
                Sign out now
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

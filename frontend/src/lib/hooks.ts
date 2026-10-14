import { useCallback, useEffect, useRef, useState } from "react";
import { useBlocker } from "react-router-dom";

export type Load<T> =
  | { status: "loading" }
  | { status: "error"; error: unknown }
  | { status: "ready"; data: T };

/**
 * Loads something once when the page opens (and again when asked to). There is no timer and no polling anywhere: a background call
 * would keep resetting the 30-minute idle timeout, so the session would never end by itself.
 */
export function useLoad<T>(load: () => Promise<T>, deps: readonly unknown[] = []): { state: Load<T>; reload: () => void } {
  const [round, setRound] = useState(0);
  const [result, setResult] = useState<{ key: string; state: Load<T> } | null>(null);
  // What the page is asking for right now: a result for anything else (an earlier id, an earlier page) is not shown.
  const key = JSON.stringify([round, ...deps]);

  useEffect(() => {
    let current = true;
    load().then(
      (data) => {
        if (current) {
          setResult({ key, state: { status: "ready", data } });
        }
      },
      (error: unknown) => {
        if (current) {
          setResult({ key, state: { status: "error", error } });
        }
      },
    );

    return () => {
      current = false;
    };
    // The load function is new on every render; the key says when what it asks for has really changed.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);

  const reload = useCallback(() => {
    setRound((value) => value + 1);
  }, []);

  const state: Load<T> = result?.key === key ? result.state : { status: "loading" };

  return { state, reload };
}

/**
 * Wraps a form's submit so it can only be in flight once. While it runs, `pending` is true (the button is disabled), and a second
 * click or a second Enter press does nothing at all, so nothing is ever sent twice.
 */
export function useSubmit<A extends unknown[]>(action: (...args: A) => Promise<void>): { pending: boolean; run: (...args: A) => Promise<void> } {
  const [pending, setPending] = useState(false);
  const inFlight = useRef(false);

  const run = useCallback(
    async (...args: A) => {
      if (inFlight.current) {
        return;
      }

      inFlight.current = true;
      setPending(true);
      try {
        await action(...args);
      } finally {
        inFlight.current = false;
        setPending(false);
      }
    },
    [action],
  );

  return { pending, run };
}

/**
 * Warns before the page is left while something is on it that will not be shown again (recovery codes, a setup link):
 * closing the tab, reloading, or moving to another screen in the app.
 */
export function useLeaveWarning(active: boolean, message = "This will not be shown again. Leave without keeping it?"): void {
  useEffect(() => {
    if (!active) {
      return;
    }

    const warn = (event: BeforeUnloadEvent) => {
      event.preventDefault();
    };
    window.addEventListener("beforeunload", warn);

    return () => {
      window.removeEventListener("beforeunload", warn);
    };
  }, [active]);

  const blocker = useBlocker(active);
  useEffect(() => {
    if (blocker.state !== "blocked") {
      return;
    }

    if (window.confirm(message)) {
      blocker.proceed();
    } else {
      blocker.reset();
    }
  }, [blocker, message]);
}

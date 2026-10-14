import { useEffect, useImperativeHandle, useRef, useState, type Ref } from "react";
import { TURNSTILE_SCRIPT, turnstileSiteKey } from "../lib/config";

interface TurnstileApi {
  render: (container: HTMLElement, options: Record<string, unknown>) => string;
  reset: (widgetId?: string) => void;
  remove: (widgetId?: string) => void;
}

declare global {
  interface Window {
    turnstile?: TurnstileApi;
  }
}

export interface TurnstileHandle {
  /** A token works once. After each attempt, ask for a new one. */
  reset: () => void;
}

let scriptLoading: Promise<void> | null = null;

/** Loads Cloudflare's script, once. Turnstile is the one outside origin the app is allowed to use. */
function loadTurnstile(): Promise<void> {
  if (window.turnstile !== undefined) {
    return Promise.resolve();
  }

  scriptLoading ??= new Promise<void>((resolve, reject) => {
    const script = document.createElement("script");
    script.src = TURNSTILE_SCRIPT;
    script.async = true;
    script.onload = () => {
      resolve();
    };
    script.onerror = () => {
      scriptLoading = null;
      reject(new Error("The security check could not be loaded."));
    };
    document.head.append(script);
  });

  return scriptLoading;
}

/** The Cloudflare Turnstile check. It tells the form its token, or null when it has not been passed (or has run out). */
export function Turnstile({ onToken, ref }: { onToken: (token: string | null) => void; ref?: Ref<TurnstileHandle> }) {
  const container = useRef<HTMLDivElement>(null);
  const widget = useRef<string | null>(null);
  const [failed, setFailed] = useState(false);
  const siteKey = turnstileSiteKey();
  const report = useRef(onToken);
  useEffect(() => {
    report.current = onToken;
  });

  useImperativeHandle(ref, () => ({
    reset: () => {
      report.current(null);
      if (widget.current !== null) {
        window.turnstile?.reset(widget.current);
      }
    },
  }));

  useEffect(() => {
    if (siteKey === undefined) {
      return;
    }

    let active = true;
    loadTurnstile().then(
      () => {
        if (!active || container.current === null || window.turnstile === undefined) {
          return;
        }

        widget.current = window.turnstile.render(container.current, {
          sitekey: siteKey,
          callback: (token: string) => {
            report.current(token);
          },
          "expired-callback": () => {
            report.current(null);
          },
          "error-callback": () => {
            report.current(null);
            setFailed(true);
          },
        });
      },
      () => {
        if (active) {
          setFailed(true);
        }
      },
    );

    return () => {
      active = false;
      if (widget.current !== null) {
        window.turnstile?.remove(widget.current);
        widget.current = null;
      }
    };
  }, [siteKey]);

  if (siteKey === undefined) {
    return <p className="alert-error">The security check is not set up on this site, so this form cannot be used.</p>;
  }

  return (
    <div className="mb-4">
      <div ref={container} />
      {failed ? <p className="field-error">The security check did not load. Reload the page and try again.</p> : null}
    </div>
  );
}

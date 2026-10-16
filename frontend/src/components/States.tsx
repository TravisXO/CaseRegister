import { Component, type ErrorInfo, type ReactNode } from "react";
import { describeError, ApiError } from "../api/client";
import type { Load } from "../lib/hooks";
import { DemoNotice } from "./DemoNotice";

export function Loading({ what = "Loading" }: { what?: string }) {
  return (
    <p role="status" className="py-6 text-slate-700">
      {what}…
    </p>
  );
}

export function Empty({ children }: { children: ReactNode }) {
  return <p className="card text-slate-700">{children}</p>;
}

/** What went wrong, and the trace id to quote. A 403 is a plain "you cannot use this"; a 429 says when to try again. */
export function ErrorPanel({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  if (error instanceof ApiError && error.status === 403) {
    return <Forbidden />;
  }

  const { message, traceId } = describeError(error);

  return (
    <div role="alert" className="alert-error">
      <p>{message}</p>
      {traceId === undefined ? null : <p className="mt-1">Reference: {traceId}</p>}
      {onRetry === undefined ? null : (
        <button type="button" className="btn-secondary mt-2" onClick={onRetry}>
          Try again
        </button>
      )}
    </div>
  );
}

export function Forbidden() {
  return (
    <div className="page">
      <h1 className="heading">You do not have access to this</h1>
      <p>Your account is not allowed to use this page. If you think it should be, ask an admin.</p>
    </div>
  );
}

/** Shows the right thing for a load: waiting, the problem, or the page itself. */
export function Loaded<T>({ load, retry, what, children }: { load: Load<T>; retry?: () => void; what?: string; children: (data: T) => ReactNode }) {
  if (load.status === "loading") {
    return <Loading {...(what === undefined ? {} : { what })} />;
  }

  if (load.status === "error") {
    return <ErrorPanel error={load.error} {...(retry === undefined ? {} : { onRetry: retry })} />;
  }

  return <>{children(load.data)}</>;
}

/** The last resort: something unexpected broke while drawing a page. It says so, plainly, and shows nothing of the cause. */
export class ErrorBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  override state = { failed: false };

  static getDerivedStateFromError(): { failed: boolean } {
    return { failed: true };
  }

  override componentDidCatch(error: Error, info: ErrorInfo): void {
    // The browser console is where a developer looks; nothing is sent anywhere.
    console.error(error, info.componentStack);
  }

  override render(): ReactNode {
    if (!this.state.failed) {
      return this.props.children;
    }

    return (
      <>
        <DemoNotice />
        <div role="alert" className="page">
          <h1 className="heading">Something went wrong</h1>
          <p>This page could not be shown. Reload it, and if it happens again tell an admin what you were doing.</p>
          <button
            type="button"
            className="btn-primary mt-4"
            onClick={() => {
              window.location.reload();
            }}
          >
            Reload
          </button>
        </div>
      </>
    );
  }
}

// The server behind the site sleeps when nobody has used it for a while and takes up to a minute to wake. The proxy turns every sign of that into
// one answer: a 503 whose title is "The server is starting up", with Retry-After: 30 (functions/api/[[path]].ts). This is what the page does about it.
//
//  - It says so in a status message (StartingUpNotice), with a Retry button.
//  - A read (GET) is asked for again, once, after 30 seconds: reading twice changes nothing. The caller just waits a little longer.
//  - Anything that changes something (POST, PUT, PATCH, DELETE) is NEVER asked again by the page. It might have been done before the answer was
//    lost; only the person can decide to do it again. The message says the change was not confirmed.
//
// The wait below is one of the two timers in the application (the other is the session timeout warning): a single timeout, never a repeating one.

/** The title the proxy gives. The page recognises the situation by it. */
export const STARTING_UP_TITLE = "The server is starting up";

/** How long a read waits before the one automatic retry. The same as the proxy's Retry-After. */
export const AUTO_RETRY_MS = 30_000;

export type StartupState = "none" | "waiting" | "unavailable";

let state: StartupState = "none";
const listeners = new Set<() => void>();
const wakers = new Set<() => void>();

export function subscribe(listener: () => void): () => void {
  listeners.add(listener);

  return () => {
    listeners.delete(listener);
  };
}

export function snapshot(): StartupState {
  return state;
}

function set(next: StartupState): void {
  if (state !== next) {
    state = next;
    for (const listener of [...listeners]) {
      listener();
    }
  }
}

export function isStartingUp(status: number, title: string): boolean {
  return status === 503 && title === STARTING_UP_TITLE;
}

/** The server answered: whatever was said about it starting up is over. */
export function clear(): void {
  set("none");
}

/** A request got the "starting up" answer. <paramref>waiting</paramref>: a read that will be asked again by itself. */
export function begin(waiting: boolean): void {
  set(waiting ? "waiting" : "unavailable");
}

/** Waits the 30 seconds (or until someone presses Retry and the server turns out to be up), once, for one read. */
export function waitBeforeRetry(): Promise<void> {
  return new Promise((resolve) => {
    const timer = setTimeout(done, AUTO_RETRY_MS);

    function done(): void {
      clearTimeout(timer);
      wakers.delete(done);
      resolve();
    }

    wakers.add(done);
  });
}

/** Ends every wait now: the server has been seen to be up. */
export function retryWaitingReadsNow(): void {
  for (const wake of [...wakers]) {
    wake();
  }
}

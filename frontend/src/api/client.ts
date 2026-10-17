// The one place the front end talks to the API. Everything goes through here, so there is one set of rules for:
//  - the anti-forgery token (fetched when first needed, sent as X-CSRF-TOKEN on every POST, PUT, PATCH and DELETE, fetched again after sign-in),
//  - the sign-in cookie (same origin only: the browser sends it, this code never sees it),
//  - errors (a ProblemDetails becomes an ApiError that shows a message and a trace id, and nothing else).

const UNSAFE_METHODS = new Set(["POST", "PUT", "PATCH", "DELETE"]);
const CSRF_HEADER = "X-CSRF-TOKEN";
const CSRF_PATH = "/api/auth/csrf";
const ANTIFORGERY_DETAIL = "The anti-forgery token is missing or invalid.";

export interface ProblemShape {
  status: number;
  title: string;
  detail?: string;
  traceId?: string;
  retryAfterSeconds?: number;
  /** Field name to messages, for a validation problem. The names are the API's (for example "Email"). */
  fieldErrors: Record<string, string[]>;
  /** Rules that stopped an admin action, each with a code and a plain message. */
  problems: { code: string; message: string }[];
}

export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail: string | undefined;
  readonly traceId: string | undefined;
  readonly retryAfterSeconds: number | undefined;
  readonly fieldErrors: Record<string, string[]>;
  readonly problems: { code: string; message: string }[];

  constructor(shape: ProblemShape) {
    super(shape.detail ?? shape.title);
    this.name = "ApiError";
    this.status = shape.status;
    this.title = shape.title;
    this.detail = shape.detail;
    this.traceId = shape.traceId;
    this.retryAfterSeconds = shape.retryAfterSeconds;
    this.fieldErrors = shape.fieldErrors;
    this.problems = shape.problems;
  }
}

/** What a person is shown about an error: a message and a trace id they can quote. Never a stack, never a raw response. */
export interface ErrorMessage {
  message: string;
  traceId?: string;
}

export function describeError(error: unknown): ErrorMessage {
  if (!(error instanceof ApiError)) {
    return { message: "Something went wrong. Try again." };
  }

  const message = error.status === 429 ? waitMessage(error.retryAfterSeconds) : (error.detail ?? error.title);

  return error.traceId === undefined ? { message } : { message, traceId: error.traceId };
}

export function waitMessage(seconds: number | undefined): string {
  if (seconds === undefined) {
    return "Too many attempts. Wait a little and try again.";
  }

  if (seconds < 90) {
    return `Too many attempts. Try again in ${seconds} ${seconds === 1 ? "second" : "seconds"}.`;
  }

  const minutes = Math.ceil(seconds / 60);

  return `Too many attempts. Try again in ${minutes} minutes.`;
}

export type UnauthorisedPolicy = "session-ended" | "report";

export interface RequestOptions {
  body?: unknown;
  /**
   * What a 401 means. For most calls it means the session has ended (idle for 30 minutes, or past 8 hours): everything held in
   * memory is dropped and the person goes back to sign-in. For the sign-in calls themselves it just means "that did not work".
   */
  unauthorised?: UnauthorisedPolicy;
}

let csrfToken: string | null = null;
let csrfRequest: Promise<string> | null = null;
let sessionEndedHandler: (() => void) | null = null;
let successHandler: (() => void) | null = null;

/** Called when the API says the session is no longer there. The session provider sets this. */
export function onSessionEnded(handler: (() => void) | null): void {
  sessionEndedHandler = handler;
}

/**
 * Called after every successful answer from the API. The session timeout warning uses it to restart its clocks: an answer means the person is
 * active and the server has restarted its idle timer. It is told nothing about the answer.
 */
export function onApiSuccess(handler: (() => void) | null): void {
  successHandler = handler;
}

/** Drops the anti-forgery token. A new one is fetched with the next request that needs it. */
export function forgetCsrfToken(): void {
  csrfToken = null;
  csrfRequest = null;
}

/** Fetches a new anti-forgery token. The token is tied to who is signed in, so this is done again after signing in. */
export async function refreshCsrfToken(): Promise<string> {
  forgetCsrfToken();

  return loadCsrfToken();
}

function loadCsrfToken(): Promise<string> {
  csrfRequest ??= (async () => {
    try {
      const response = await fetch(CSRF_PATH, { credentials: "same-origin", cache: "no-store", headers: { Accept: "application/json" } });
      if (!response.ok) {
        throw await toApiError(response);
      }

      const body = (await response.json()) as { token: string };
      csrfToken = body.token;

      return body.token;
    } finally {
      csrfRequest = null;
    }
  })();

  return csrfRequest;
}

export async function request<T>(method: string, path: string, options: RequestOptions = {}): Promise<T> {
  const response = await send(method, path, options);
  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

async function send(method: string, path: string, options: RequestOptions, retried = false): Promise<Response> {
  const headers: Record<string, string> = { Accept: "application/json" };
  const unsafe = UNSAFE_METHODS.has(method);
  if (unsafe) {
    headers[CSRF_HEADER] = csrfToken ?? (await loadCsrfToken());
  }

  if (options.body !== undefined) {
    headers["Content-Type"] = "application/json";
  }

  let response: Response;
  try {
    response = await fetch(path, {
      method,
      headers,
      credentials: "same-origin",
      cache: "no-store",
      ...(options.body === undefined ? {} : { body: JSON.stringify(options.body) }),
    });
  } catch {
    throw new ApiError({ status: 0, title: "Could not reach the server", detail: "Could not reach the server. Check your connection and try again.", fieldErrors: {}, problems: [] });
  }

  if (response.ok) {
    successHandler?.();

    return response;
  }

  const error = await toApiError(response);

  // A token that has gone stale (after a sign-in somewhere else, say) is refused before anything is done, so asking again is safe.
  if (unsafe && !retried && error.status === 400 && error.detail === ANTIFORGERY_DETAIL) {
    await refreshCsrfToken();

    return send(method, path, options, true);
  }

  if (error.status === 401 && (options.unauthorised ?? "session-ended") === "session-ended") {
    forgetCsrfToken();
    sessionEndedHandler?.();
  }

  throw error;
}

async function toApiError(response: Response): Promise<ApiError> {
  let body: Record<string, unknown> = {};
  try {
    body = (await response.json()) as Record<string, unknown>;
  } catch {
    // Not JSON (a proxy's page, say). Nothing from it is shown.
  }

  const retryAfter = Number.parseInt(response.headers.get("Retry-After") ?? "", 10);
  const text = (value: unknown): string | undefined => (typeof value === "string" && value !== "" ? value : undefined);
  const detail = text(body.detail);
  const traceId = text(body.traceId);

  return new ApiError({
    status: response.status,
    title: text(body.title) ?? "Something went wrong",
    ...(detail === undefined ? {} : { detail }),
    ...(traceId === undefined ? {} : { traceId }),
    ...(Number.isNaN(retryAfter) ? {} : { retryAfterSeconds: retryAfter }),
    fieldErrors: asFieldErrors(body.errors),
    problems: asProblems(body.problems),
  });
}

function asFieldErrors(value: unknown): Record<string, string[]> {
  if (typeof value !== "object" || value === null) {
    return {};
  }

  const result: Record<string, string[]> = {};
  for (const [field, messages] of Object.entries(value)) {
    if (Array.isArray(messages)) {
      result[field] = messages.filter((message): message is string => typeof message === "string");
    }
  }

  return result;
}

function asProblems(value: unknown): { code: string; message: string }[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value.flatMap((item: unknown) => {
    const entry = item as { code?: unknown; message?: unknown };

    return typeof entry.code === "string" && typeof entry.message === "string" ? [{ code: entry.code, message: entry.message }] : [];
  });
}

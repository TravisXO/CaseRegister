/**
 * The Cloudflare Pages Function that sends /api/* on to the API on Render. It is the only way in: the API refuses any request that does
 * not carry the secret this adds.
 *
 *  - It forwards the method, the body, the cookies and a short list of other headers, and nothing else. Whatever else a visitor sends
 *    (including their own copies of the secret header, X-Forwarded-For and CF-Connecting-IP) is never passed on.
 *  - The visitor's address comes from CF-Connecting-IP, which only Cloudflare's edge sets, and goes to the API in X-Client-Ip. The API believes
 *    it only on a request that carries the secret.
 *  - The visitor's city and country come from request.cf (Cloudflare's own lookup, so no database and no dependency here) and go to the API
 *    in X-Client-Geo, trusted by the API on the same terms as X-Client-Ip. Only the two-letter country and the city name are read: the
 *    coordinates that request.cf also holds are never touched.
 *  - Set-Cookie comes back untouched, so the __Host- cookies work.
 *  - The body is capped, the call has a timeout, and no body, cookie or secret is ever logged. (There is no logging here at all.)
 *  - When the API is asleep or starting (Render's free plan stops it when idle and takes about a minute to wake): a timeout, a connection
 *    that fails, a 502, 503 or 504, or an answer that is not JSON (an error page from Render's own front door) all become the same
 *    503 problem, "The server is starting up", with Retry-After: 30. The page shows it as a status message and asks again once, for reads only.
 *  - It answers only on the production hostname, so a preview deployment of this site cannot reach the production API.
 *
 * Settings (Cloudflare Pages, Production environment only): API_ORIGIN, PROXY_SECRET, PRODUCTION_HOSTNAME; UPSTREAM_TIMEOUT_MS is optional.
 */

export interface ProxyEnv {
  /** The API's address, such as https://caseregister-api.onrender.com (https, no path). */
  API_ORIGIN?: string;
  /** The shared secret. The same value as Proxy__Secret (or Proxy__NextSecret) on Render. */
  PROXY_SECRET?: string;
  /** The one hostname this site answers /api on, such as caseregister.pages.dev. Preview deployments have other hostnames. */
  PRODUCTION_HOSTNAME?: string;
  /** How long to wait for the API, in milliseconds. Render's free plan can take about a minute to wake. */
  UPSTREAM_TIMEOUT_MS?: string;
}

interface ProxyContext {
  request: Request;
  env: ProxyEnv;
}

/** The API's own limit for a request body is 64 KiB; nothing larger is worth sending. */
export const MAX_BODY_BYTES = 64 * 1024;
export const DEFAULT_TIMEOUT_MS = 75_000;
export const SECRET_HEADER = "X-Proxy-Secret";
export const CLIENT_IP_HEADER = "X-Client-Ip";
export const CLIENT_GEO_HEADER = "X-Client-Geo";
const MIN_SECRET_LENGTH = 32;
const MAX_CITY_LENGTH = 100;

const METHODS_WITH_BODY = new Set(["POST", "PUT", "PATCH", "DELETE"]);
const METHODS_ALLOWED = new Set(["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"]);

/** What is passed on from the visitor's request. Everything else is dropped, which is how forwarding headers and fake secrets never get through. */
const REQUEST_HEADERS_FORWARDED = ["accept", "accept-language", "content-type", "cookie", "x-csrf-token", "user-agent"];

/** Headers that describe one connection, not the answer. */
const RESPONSE_HEADERS_DROPPED = ["connection", "keep-alive", "proxy-authenticate", "proxy-authorization", "te", "trailer", "transfer-encoding", "upgrade", "server", "x-powered-by"];

/** What a page looks for to know the server is on its way up, rather than that something is wrong. */
export const STARTING_UP_TITLE = "The server is starting up";
export const STARTING_UP_RETRY_AFTER_SECONDS = 30;

function problem(status: number, title: string, extraHeaders: Record<string, string> = {}): Response {
  return new Response(JSON.stringify({ title, status }), {
    status,
    headers: { "Content-Type": "application/problem+json", "Cache-Control": "no-store", "X-Robots-Tag": "noindex, nofollow", ...extraHeaders },
  });
}

function startingUp(): Response {
  return problem(503, STARTING_UP_TITLE, { "Retry-After": String(STARTING_UP_RETRY_AFTER_SECONDS) });
}

/** True for an answer that did not come from the API: it has a content type, and it is not JSON (an HTML error page from the host in front of it). Empty answers have none. */
function isNotFromTheApi(answer: Response): boolean {
  const type = (answer.headers.get("content-type") ?? "").toLowerCase();

  return type !== "" && !/\bjson\b/.test(type);
}

/** What Cloudflare knows about where a request came from. Only the two fields below are ever read. */
interface CloudflareRequest extends Request {
  cf?: { country?: unknown; city?: unknown };
}

/**
 * "ZM|Lusaka": the two-letter country, a bar, and the city with percent-encoding (a city name can hold any letters). Null when Cloudflare gave
 * no usable country. The city is left out if it is missing or absurdly long. Nothing else about the place is sent: no region, no postcode,
 * no coordinates.
 */
export function geoOf(request: Request): string | null {
  const cf = (request as CloudflareRequest).cf;
  const country = typeof cf?.country === "string" ? cf.country.toUpperCase() : "";
  if (!/^[A-Z]{2}$/.test(country)) {
    return null;
  }

  const city = typeof cf?.city === "string" ? cf.city.trim() : "";

  return `${country}|${city.length > 0 && city.length <= MAX_CITY_LENGTH ? encodeURIComponent(city) : ""}`;
}

/** The API origin, or null if it is not an https address with no path (so a mistake in the setting cannot send requests somewhere else). */
function apiOrigin(env: ProxyEnv): URL | null {
  try {
    const origin = new URL(env.API_ORIGIN ?? "");

    return origin.protocol === "https:" && origin.pathname === "/" && origin.search === "" && origin.username === "" ? origin : null;
  } catch {
    return null;
  }
}

function timeoutFrom(env: ProxyEnv): number {
  const asked = Number(env.UPSTREAM_TIMEOUT_MS);

  return Number.isFinite(asked) && asked >= 1000 && asked <= 120_000 ? asked : DEFAULT_TIMEOUT_MS;
}

/** The whole body, or null if it is larger than the limit (reading stops as soon as it is). */
async function readCapped(body: ReadableStream<Uint8Array> | null, limit: number): Promise<Uint8Array<ArrayBuffer> | null> {
  if (body === null) {
    return new Uint8Array(0);
  }

  const reader = body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) {
      break;
    }

    total += value.byteLength;
    if (total > limit) {
      await reader.cancel();

      return null;
    }

    chunks.push(value);
  }

  const all = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    all.set(chunk, offset);
    offset += chunk.byteLength;
  }

  return all;
}

export async function handle(request: Request, env: ProxyEnv): Promise<Response> {
  const origin = apiOrigin(env);
  const secret = env.PROXY_SECRET ?? "";
  if (origin === null || secret.length < MIN_SECRET_LENGTH || (env.PRODUCTION_HOSTNAME ?? "") === "") {
    // Not set up (or not meant to run here): say so without saying which setting.
    return problem(503, "This site is not available");
  }

  const url = new URL(request.url);
  if (url.hostname.toLowerCase() !== (env.PRODUCTION_HOSTNAME ?? "").toLowerCase()) {
    return problem(404, "Not found");
  }

  // Only /api/..., and the address is built by joining text to the API's origin, so nothing in the path can name another host ("//host/...").
  if (!url.pathname.startsWith("/api/")) {
    return problem(404, "Not found");
  }

  const method = request.method.toUpperCase();
  if (!METHODS_ALLOWED.has(method)) {
    return problem(405, "Method not allowed");
  }

  const target = new URL(origin.origin + url.pathname + url.search);
  if (target.origin !== origin.origin) {
    return problem(404, "Not found");
  }

  const headers = new Headers();
  for (const name of REQUEST_HEADERS_FORWARDED) {
    const value = request.headers.get(name);
    if (value !== null) {
      headers.set(name, value);
    }
  }

  headers.set(SECRET_HEADER, secret);

  // Set by Cloudflare's edge, never by the visitor. Missing only when this runs outside Cloudflare (a local test).
  const visitor = request.headers.get("CF-Connecting-IP");
  if (visitor !== null && visitor !== "") {
    headers.set(CLIENT_IP_HEADER, visitor);
  }

  const geo = geoOf(request);
  if (geo !== null) {
    headers.set(CLIENT_GEO_HEADER, geo);
  }

  let body: Uint8Array<ArrayBuffer> | undefined;
  if (METHODS_WITH_BODY.has(method)) {
    const declared = Number(request.headers.get("content-length") ?? "0");
    if (Number.isFinite(declared) && declared > MAX_BODY_BYTES) {
      return problem(413, "Request too large");
    }

    const read = await readCapped(request.body, MAX_BODY_BYTES);
    if (read === null) {
      return problem(413, "Request too large");
    }

    body = read.byteLength > 0 ? read : undefined;
  }

  let upstream: Response;
  try {
    upstream = await fetch(target, { method, headers, body, redirect: "manual", signal: AbortSignal.timeout(timeoutFrom(env)) });
  } catch {
    // Nothing about the failure is passed on, and nothing is logged: the request held cookies and perhaps a body. A timeout and a connection that
    // cannot be made look the same from outside: the API is asleep or waking.
    return startingUp();
  }

  // The host in front of the API answers 502, 503 or 504 while it wakes, and with a page of its own. Neither is passed on.
  if (upstream.status === 502 || upstream.status === 503 || upstream.status === 504 || isNotFromTheApi(upstream)) {
    await upstream.body?.cancel();

    return startingUp();
  }

  // Headers are copied as they are, so every Set-Cookie (there can be several) arrives exactly as the API wrote it.
  const answer = new Headers(upstream.headers);
  for (const name of RESPONSE_HEADERS_DROPPED) {
    answer.delete(name);
  }

  answer.set("X-Robots-Tag", "noindex, nofollow");

  return new Response(upstream.body, { status: upstream.status, statusText: upstream.statusText, headers: answer });
}

export const onRequest = ({ request, env }: ProxyContext): Promise<Response> => handle(request, env);

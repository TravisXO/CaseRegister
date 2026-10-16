import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CLIENT_IP_HEADER, DEFAULT_TIMEOUT_MS, MAX_BODY_BYTES, SECRET_HEADER, handle, onRequest, type ProxyEnv } from "./[[path]]";

const SECRET = "test-secret-test-secret-test-secret-test-secret";
const HOST = "caseregister.example.test";

const goodEnv: ProxyEnv = { API_ORIGIN: "https://api.example.test", PROXY_SECRET: SECRET, PRODUCTION_HOSTNAME: HOST };

const fetchMock = vi.fn<(input: URL, init: RequestInit) => Promise<Response>>();

function lastCall(): { url: string; init: RequestInit; headers: Headers } {
  const call = fetchMock.mock.calls.at(-1);
  if (call === undefined) {
    throw new Error("the API was not called");
  }

  return { url: call[0].toString(), init: call[1], headers: new Headers(call[1].headers) };
}

const answer = (body = "{}", init: ResponseInit = {}) => () => Promise.resolve(new Response(body, { status: 200, ...init }));

function send(path: string, init: RequestInit = {}, env: ProxyEnv = goodEnv, host = HOST): Promise<Response> {
  return handle(new Request(`https://${host}${path}`, init), env);
}

beforeEach(() => {
  fetchMock.mockReset();
  fetchMock.mockImplementation(answer());
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("what is forwarded", () => {
  it("sends the method, the path with its query, the body, the cookies and the anti-forgery token on to the API, with the secret added", async () => {
    const response = await send("/api/auth/sign-in?next=1", {
      method: "POST",
      body: JSON.stringify({ email: "ada@example.test" }),
      headers: { "Content-Type": "application/json", Cookie: "__Host-cr=abc; __Host-csrf=def", "X-CSRF-TOKEN": "token-1", Accept: "application/json", "Accept-Language": "en-GB", "User-Agent": "TestBrowser/1", "CF-Connecting-IP": "203.0.113.9" },
    });

    const { url, init, headers } = lastCall();
    expect(response.status).toBe(200);
    expect(url).toBe("https://api.example.test/api/auth/sign-in?next=1");
    expect(init.method).toBe("POST");
    expect(new TextDecoder().decode(init.body as Uint8Array)).toBe('{"email":"ada@example.test"}');
    expect(headers.get("cookie")).toBe("__Host-cr=abc; __Host-csrf=def");
    expect(headers.get("x-csrf-token")).toBe("token-1");
    expect(headers.get("content-type")).toBe("application/json");
    expect(headers.get("accept")).toBe("application/json");
    expect(headers.get("user-agent")).toBe("TestBrowser/1");
    expect(headers.get(SECRET_HEADER)).toBe(SECRET);
    expect(headers.get(CLIENT_IP_HEADER)).toBe("203.0.113.9");
  });

  it.each(["GET", "HEAD", "DELETE", "PUT", "PATCH"])("forwards a %s", async (method) => {
    await send("/api/me/profile", { method });

    expect(lastCall().init.method).toBe(method);
  });

  it("does not follow a redirect itself, and always gives the call a time limit", async () => {
    await send("/api/auth/session");

    const { init } = lastCall();
    expect(init.redirect).toBe("manual");
    expect(init.signal).toBeInstanceOf(AbortSignal);
  });

  it("is the handler Pages calls for the route", async () => {
    const response = await onRequest({ request: new Request(`https://${HOST}/api/auth/csrf`), env: goodEnv });

    expect(response.status).toBe(200);
  });
});

describe("what is never forwarded", () => {
  const spoofed = {
    "X-Proxy-Secret": "the-visitors-own-guess",
    "X-Client-Ip": "6.6.6.6",
    "X-Forwarded-For": "6.6.6.6, 7.7.7.7",
    "X-Forwarded-Host": "evil.example",
    "X-Forwarded-Proto": "http",
    Forwarded: "for=6.6.6.6",
    "X-Real-IP": "6.6.6.6",
    "True-Client-IP": "6.6.6.6",
    "X-Original-URL": "/admin",
    "X-HTTP-Method-Override": "DELETE",
    Authorization: "Bearer stolen",
    Origin: "https://evil.example",
    Referer: "https://evil.example/page",
  };

  it("drops every header that is not on the short list, including forwarding headers and the visitor's own copy of the secret header", async () => {
    await send("/api/auth/session", { headers: { ...spoofed, Cookie: "a=b" } });

    const { headers } = lastCall();
    for (const name of Object.keys(spoofed).filter((header) => header !== "X-Proxy-Secret" && header !== "X-Client-Ip")) {
      expect(headers.has(name), name).toBe(false);
    }

    expect(headers.get(SECRET_HEADER)).toBe(SECRET); // ours, not the visitor's
    expect(headers.has(CLIENT_IP_HEADER)).toBe(false); // nothing from Cloudflare to report, and the visitor's own is not believed
    expect(headers.get("cookie")).toBe("a=b");
  });

  it("takes the visitor's address from CF-Connecting-IP alone, and says it under our own header name", async () => {
    await send("/api/auth/session", { headers: { ...spoofed, "CF-Connecting-IP": "203.0.113.9" } });

    const { headers } = lastCall();
    expect(headers.get(CLIENT_IP_HEADER)).toBe("203.0.113.9");
    expect(headers.has("cf-connecting-ip")).toBe(false);
  });

  it("does not send the visitor's own secret header even if it holds the right secret", async () => {
    await send("/api/auth/session", { headers: { "X-Proxy-Secret": SECRET, "X-Proxy-Secret-2": "x" } });

    const sent = [...lastCall().headers.entries()].filter(([name]) => name.startsWith("x-proxy-secret"));
    expect(sent).toEqual([["x-proxy-secret", SECRET]]); // exactly one: the one this function set
  });
});

describe("what comes back", () => {
  it("passes every Set-Cookie through exactly as the API wrote it, so the __Host- cookies keep working", async () => {
    const cookies = ["__Host-cr=abc123; path=/; secure; httponly; samesite=strict", "__Host-csrf=def456; path=/; secure; httponly; samesite=strict"];
    fetchMock.mockImplementation(() => {
      const headers = new Headers({ "Content-Type": "application/json" });
      for (const cookie of cookies) {
        headers.append("Set-Cookie", cookie);
      }

      return Promise.resolve(new Response("{}", { status: 200, headers }));
    });

    const response = await send("/api/auth/sign-in", { method: "POST", body: "{}", headers: { "Content-Type": "application/json" } });

    expect(response.headers.getSetCookie()).toEqual(cookies);
  });

  it("passes the status, the body and the headers that matter, including a rate limit's Retry-After", async () => {
    fetchMock.mockImplementation(answer('{"title":"Too many requests"}', { status: 429, headers: { "Content-Type": "application/problem+json", "Retry-After": "42", "Cache-Control": "no-store" } }));

    const response = await send("/api/auth/sign-in", { method: "POST", body: "{}", headers: { "Content-Type": "application/json" } });

    expect(response.status).toBe(429);
    expect(response.headers.get("retry-after")).toBe("42");
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(response.headers.get("content-type")).toBe("application/problem+json");
    expect(await response.text()).toBe('{"title":"Too many requests"}');
  });

  it("drops what describes the connection or the server, and asks search engines to stay away", async () => {
    fetchMock.mockImplementation(answer("{}", { headers: { Server: "Kestrel", "X-Powered-By": "ASP.NET", Connection: "keep-alive", "Keep-Alive": "timeout=5", "Content-Type": "application/json" } }));

    const response = await send("/api/auth/session");

    for (const name of ["server", "x-powered-by", "connection", "keep-alive"]) {
      expect(response.headers.has(name), name).toBe(false);
    }

    expect(response.headers.get("x-robots-tag")).toBe("noindex, nofollow");
    expect(response.headers.get("content-type")).toBe("application/json");
  });
});

describe("only /api/ on the production hostname", () => {
  it.each(["/", "/profile", "/api", "/apix/auth", "/robots.txt", "//evil.example/api/x", "/api/../admin", "/api/%2e%2e/admin", "/api\\..\\admin", "/health/live"])("does not forward %s", async (path) => {
    const response = await handle(new Request(`https://${HOST}${path}`), goodEnv);

    expect(response.status).toBe(404);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("can never be pointed at another host by a crafted path", async () => {
    for (const path of ["/api//evil.example/x", "/api/@evil.example/x", "/api/https://evil.example/x"]) {
      await send(path);
      expect(new URL(lastCall().url).host).toBe("api.example.test");
    }
  });

  it("refuses a preview deployment's hostname, so only production reaches the production API", async () => {
    for (const host of ["abc123.caseregister.example.test", "feature-branch.caseregister.example.test", "evil.example", "caseregister.example.test.evil.example"]) {
      const response = await send("/api/auth/session", {}, goodEnv, host);

      expect(response.status, host).toBe(404);
    }

    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("answers on the production hostname whatever its capitals", async () => {
    const response = await send("/api/auth/session", {}, goodEnv, "CaseRegister.Example.Test");

    expect(response.status).toBe(200);
  });

  it.each(["OPTIONS", "PROPFIND", "PURGE"])("refuses the method %s", async (method) => {
    const response = await send("/api/auth/session", { method });

    expect(response.status).toBe(405);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

describe("when it is not set up", () => {
  it.each<[string, ProxyEnv]>([
    ["no API address", { ...goodEnv, API_ORIGIN: undefined }],
    ["an http API address", { ...goodEnv, API_ORIGIN: "http://api.example.test" }],
    ["an API address with a path", { ...goodEnv, API_ORIGIN: "https://api.example.test/some/path" }],
    ["an API address that is not an address", { ...goodEnv, API_ORIGIN: "not a url" }],
    ["an API address with a password in it", { ...goodEnv, API_ORIGIN: "https://user:pass@api.example.test" }],
    ["no secret", { ...goodEnv, PROXY_SECRET: undefined }],
    ["a short secret", { ...goodEnv, PROXY_SECRET: "too-short" }],
    ["no production hostname", { ...goodEnv, PRODUCTION_HOSTNAME: undefined }],
    ["an empty production hostname", { ...goodEnv, PRODUCTION_HOSTNAME: "" }],
  ])("sends nothing anywhere with %s, and says nothing about why", async (_name, env) => {
    const response = await send("/api/auth/session", {}, env);

    expect(response.status).toBe(503);
    expect(fetchMock).not.toHaveBeenCalled();
    const text = await response.text();
    for (const word of ["API_ORIGIN", "PROXY_SECRET", "PRODUCTION_HOSTNAME", SECRET, "api.example.test"]) {
      expect(text).not.toContain(word);
    }
  });
});

describe("limits", () => {
  it("refuses a body that says it is larger than the limit without reading it", async () => {
    const response = await send("/api/auth/sign-in", { method: "POST", body: "x", headers: { "Content-Length": String(MAX_BODY_BYTES + 1) } });

    expect(response.status).toBe(413);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("cuts off a body that turns out larger than the limit as it is read, whatever it said", async () => {
    const chunk = new Uint8Array(16 * 1024);
    let sent = 0;
    const stream = new ReadableStream<Uint8Array>({
      pull(controller) {
        if (sent > 6) {
          controller.close();
          return;
        }

        sent += 1;
        controller.enqueue(chunk);
      },
    });

    const response = await handle(new Request(`https://${HOST}/api/auth/sign-in`, { method: "POST", body: stream, duplex: "half" } as RequestInit), goodEnv);

    expect(response.status).toBe(413);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("lets a body of exactly the limit through", async () => {
    const response = await send("/api/auth/sign-in", { method: "POST", body: new Uint8Array(MAX_BODY_BYTES), headers: { "Content-Type": "application/json" } });

    expect(response.status).toBe(200);
    expect((lastCall().init.body as Uint8Array).byteLength).toBe(MAX_BODY_BYTES);
  });

  it("sends no body at all when there was none", async () => {
    await send("/api/auth/sign-out", { method: "POST" });

    expect(lastCall().init.body).toBeUndefined();
  });

  it.each([
    [undefined, DEFAULT_TIMEOUT_MS],
    ["5000", 5000],
    ["120000", 120_000],
    ["10", DEFAULT_TIMEOUT_MS],
    ["999999", DEFAULT_TIMEOUT_MS],
    ["soon", DEFAULT_TIMEOUT_MS],
  ])("waits %s ms for the API: %s", async (setting, expected) => {
    const timeout = vi.spyOn(AbortSignal, "timeout");

    await send("/api/auth/session", {}, { ...goodEnv, UPSTREAM_TIMEOUT_MS: setting });

    expect(timeout).toHaveBeenCalledWith(expected);
  });

  it("is a 504 when the API does not answer in time and a 502 when it cannot be reached, with nothing about why", async () => {
    fetchMock.mockRejectedValueOnce(new DOMException("The operation timed out at https://api.example.test with cookie abc", "TimeoutError"));
    const slow = await send("/api/auth/session", { headers: { Cookie: "__Host-cr=abc" } });
    fetchMock.mockRejectedValueOnce(new TypeError("fetch failed: connect ECONNREFUSED api.example.test"));
    const down = await send("/api/auth/session", { headers: { Cookie: "__Host-cr=abc" } });

    expect(slow.status).toBe(504);
    expect(down.status).toBe(502);
    for (const text of [await slow.text(), await down.text()]) {
      expect(text).not.toContain("api.example.test");
      expect(text).not.toContain("abc");
      expect(text).not.toContain("ECONNREFUSED");
    }
  });
});

describe("what it never writes down", () => {
  it("makes no log entry of any kind, whatever happens, because the requests hold cookies and bodies", async () => {
    const spies = (["log", "info", "warn", "error", "debug", "trace"] as const).map((level) => vi.spyOn(console, level).mockImplementation(() => undefined));
    fetchMock.mockRejectedValueOnce(new TypeError("fetch failed"));

    await send("/api/auth/sign-in", { method: "POST", body: '{"password":"hunter2hunter2"}', headers: { Cookie: "__Host-cr=abc", "Content-Type": "application/json" } });
    await send("/api/auth/sign-in", { method: "POST", body: '{"password":"hunter2hunter2"}', headers: { Cookie: "__Host-cr=abc", "Content-Type": "application/json" } });
    await send("/api/auth/sign-in", { method: "POST", body: "x", headers: { "Content-Length": "999999" } });
    await send("/elsewhere");
    await send("/api/auth/sign-in", {}, { ...goodEnv, PROXY_SECRET: "short" });

    for (const spy of spies) {
      expect(spy).not.toHaveBeenCalled();
    }
  });
});

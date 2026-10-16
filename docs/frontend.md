# The front end

Plain, working screens for the Phase 1 flow, in `frontend/`. React, Vite, TypeScript (strict) and Tailwind. Proper design comes later. Walk through it with [manual-testing.md](manual-testing.md).

## Running it

```bash
cd frontend
npm ci
npm run cert    # once: exports the ASP.NET Core development certificate (kept out of git)
npm run dev     # https://localhost:5173
```

Vite serves over HTTPS with the same certificate the API uses (trust it once with `dotnet dev-certs https --trust`) and proxies `/api` to the local API (`https://localhost:7205`, or `API_URL` if set), so the browser sees one site, as it will behind the Cloudflare proxy.

| Command | What it does |
| --- | --- |
| `npm run lint` | ESLint with the security, React and accessibility rules; no warnings allowed |
| `npm run typecheck` | TypeScript, strict |
| `npm test` | Vitest and Testing Library, with MSW standing in for the API |
| `npm run build` | The production build |
| `npm run check:build` | Checks the build can be served under the strict policy (no inline script or style, no source maps, no eval) |
| `npm run preview` | Serves the build over HTTPS under the production headers (`public/_headers`), to see a policy break before it ships |
| `npm run api:types` | Regenerates `src/api/schema.d.ts` from `openapi.json` |

## Types from the API

The API describes itself (OpenAPI) and the front end's types are generated from that, so the two cannot drift apart:

1. `frontend/openapi.json` is the API's own description, committed. A backend test fails if it is not what the API now says.
2. `src/api/schema.d.ts` is generated from it and committed. The CI front-end job regenerates it and fails if it differs.

When an endpoint changes:

```bash
UPDATE_OPENAPI=1 dotnet test CaseRegister.Tests --filter OpenApiDocumentTests   # PowerShell: $env:UPDATE_OPENAPI="1"
cd frontend
npm run api:types
```

Commit both files.

## Rules the code keeps

These are enforced by lint rules and tests, not just written down:

- Nothing in `localStorage`, `sessionStorage`, IndexedDB or cookies written by script. Everything about the signed-in person is in memory and is dropped on sign-out and on any 401.
- No `dangerouslySetInnerHTML`; external links carry `rel="noopener noreferrer"`; no `eval` or `new Function`.
- No repeating timers, polling or background connections: a background call would keep resetting the 30-minute idle timeout.
- The production build works under a policy with no inline script and no `eval`, where Cloudflare Turnstile is the only outside origin (`public/_headers`, tested). Source maps are off.
- The QR code for two-factor is drawn in the browser from the `otpauth` address. The secret never goes to any other service.
- All API calls go through `src/api/client.ts`: the anti-forgery token (fetched when first needed, sent on every POST, PUT, PATCH and DELETE, fetched again after signing in), same-origin cookies, and errors shown as a message and a trace id and nothing else.
- Menus show only what the person's roles allow. The API is the real gatekeeper; the menu is a convenience.

## Packages

Few on purpose. The runtime ones are `react`, `react-dom`, `react-router-dom` and `qrcode`, all MIT. The licences of everything installed were checked (MIT, ISC, Apache-2.0, BSD, with a handful of MPL-2.0 build tools and one data package under CC-BY-4.0; nothing restrictive). TypeScript is held at 5.9 and ESLint at 9 because `typescript-eslint`, `openapi-typescript` and `eslint-plugin-jsx-a11y` do not support the newer versions yet.

## Production

Cloudflare Pages serves `dist/`, applies `public/_headers` and `public/_redirects`, and a Pages Function proxies `/api/*` to the API. The Turnstile site key is set when the site is built (`VITE_TURNSTILE_SITE_KEY`). Without one, the sign-in and sign-up forms say the security check is not set up and cannot be used. `docs/deployment.md` is the runbook.

| File | What it does |
| --- | --- |
| `functions/api/[[path]].ts` | The Pages Function. Sends `/api/*` to the API with the shared secret, passes cookies and `Set-Cookie` through untouched, forwards only a short list of headers (so a visitor's own forwarding headers or copy of the secret never get through), tells the API the visitor's address from `CF-Connecting-IP`, caps the body at 64 KiB, times out after 75 seconds, answers only on `PRODUCTION_HOSTNAME`, and never logs anything. Tested in `functions/api/proxy.test.ts`. |
| `public/_headers` | The strict policy (Turnstile is the only outside origin), HSTS, nosniff, referrer and permissions policies, noindex, a year's caching for hashed files under `/assets/` and none for the page. These apply to pages and files; the Function's responses carry the API's own headers. |
| `public/_routes.json` | Only `/api/*` runs the Function, so pages and files cost no Function requests. |
| `public/_redirects` | Every other address is answered with the app, so a reload of `/profile` works. |
| `public/robots.txt`, the meta tag in `index.html` | With the `X-Robots-Tag` header, keep the demo out of search engines. |

The Function reads `API_ORIGIN`, `PROXY_SECRET`, `PRODUCTION_HOSTNAME` and (optionally) `UPSTREAM_TIMEOUT_MS` from the Pages **production** environment only. `npm run check:build` fails a build whose `dist` lacks any of the files above, lacks the noindex tag, or names the proxy secret.

Every page carries a "Demo: all data is fake" notice (`src/components/DemoNotice.tsx`), including the page shown when something breaks.

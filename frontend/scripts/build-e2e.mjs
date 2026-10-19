// A build for the browser checks (npm run e2e): the same site, with a Turnstile site key in it (the check cannot draw without one) and put in
// dist-e2e, so the real build in dist is left alone. The key is Cloudflare's published test key, which is public; the checks replace
// Cloudflare's script anyway.
import { build } from "vite";

process.env.VITE_TURNSTILE_SITE_KEY = "1x00000000000000000000AA";
await build({ build: { outDir: "dist-e2e", emptyOutDir: true } });

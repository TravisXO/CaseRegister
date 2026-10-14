/** The Cloudflare Turnstile site key (public, by design). Cloudflare's test key in development; the real one is set when the site is built. */
export function turnstileSiteKey(): string | undefined {
  const key = import.meta.env.VITE_TURNSTILE_SITE_KEY;

  return typeof key === "string" && key !== "" ? key : undefined;
}

export const TURNSTILE_SCRIPT = "https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit";

export const REASON_MIN = 10;
export const REASON_MAX = 500;

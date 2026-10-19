/** The Cloudflare Turnstile site key (public, by design). Cloudflare's test key in development; the real one is set when the site is built. */
export function turnstileSiteKey(): string | undefined {
  const key = import.meta.env.VITE_TURNSTILE_SITE_KEY;

  return typeof key === "string" && key !== "" ? key : undefined;
}

export const TURNSTILE_SCRIPT = "https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit";

export const REASON_MIN = 10;
export const REASON_MAX = 500;

/** A reset of somebody's two-factor has to say how their identity was checked. */
export const IDENTITY_REASON_MIN = 30;

/** Where to write about personal data and security (public, by design; set when the site is built). Without one the pages say to use the contact of whoever gave out the address. */
export function privacyContactEmail(): string | undefined {
  const email = import.meta.env.VITE_PRIVACY_CONTACT_EMAIL;

  return typeof email === "string" && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email) ? email : undefined;
}

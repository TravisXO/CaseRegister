// Times are always shown in Zambian time (Central Africa Time, UTC+2, no daylight saving), whatever the computer's own clock says.

const zone = "Africa/Lusaka";

const dateTime = new Intl.DateTimeFormat("en-GB", { timeZone: zone, day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit", hour12: false });
const dateOnly = new Intl.DateTimeFormat("en-GB", { timeZone: "UTC", day: "numeric", month: "short", year: "numeric" });

/** "13 Oct 2026, 14:05 (Zambian time)" from an ISO instant. */
export function formatZambianTime(iso: string | null | undefined): string {
  if (iso === null || iso === undefined || iso === "") {
    return "";
  }

  const date = new Date(iso);

  return Number.isNaN(date.getTime()) ? "" : `${dateTime.format(date)} (Zambian time)`;
}

/** "1 Jan 2026" from a calendar date such as "2026-01-01". It is a date, not a moment, so no time zone moves it. */
export function formatCalendarDate(isoDate: string): string {
  const date = new Date(`${isoDate}T00:00:00Z`);

  return Number.isNaN(date.getTime()) ? isoDate : dateOnly.format(date);
}

/** The Zambian calendar year right now. */
export function currentZambianYear(now: Date = new Date()): number {
  return Number(new Intl.DateTimeFormat("en-GB", { timeZone: zone, year: "numeric" }).format(now));
}

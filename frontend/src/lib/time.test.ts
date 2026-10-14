import { describe, expect, it } from "vitest";
import { currentZambianYear, formatCalendarDate, formatZambianTime } from "./time";

describe("Zambian time", () => {
  it("shows an instant two hours ahead of UTC, and says so", () => {
    expect(formatZambianTime("2026-10-13T08:05:00+00:00")).toBe("13 Oct 2026, 10:05 (Zambian time)");
  });

  it("moves to the next day when it is already tomorrow in Zambia", () => {
    expect(formatZambianTime("2026-12-31T22:30:00Z")).toBe("1 Jan 2027, 00:30 (Zambian time)");
  });

  it("is the same whatever offset the instant was written with", () => {
    expect(formatZambianTime("2026-10-13T10:05:00+02:00")).toBe(formatZambianTime("2026-10-13T08:05:00Z"));
    expect(formatZambianTime("2026-10-13T03:05:00-05:00")).toBe(formatZambianTime("2026-10-13T08:05:00Z"));
  });

  it("has no summer time to move it", () => {
    expect(formatZambianTime("2026-01-15T12:00:00Z")).toBe("15 Jan 2026, 14:00 (Zambian time)");
    expect(formatZambianTime("2026-07-15T12:00:00Z")).toBe("15 Jul 2026, 14:00 (Zambian time)");
  });

  it.each([null, undefined, "", "not a date"])("shows nothing for %s", (value) => {
    expect(formatZambianTime(value)).toBe("");
  });

  it("shows a calendar date as it is, with no time zone moving it", () => {
    expect(formatCalendarDate("2026-01-01")).toBe("1 Jan 2026");
    expect(formatCalendarDate("2026-12-31")).toBe("31 Dec 2026");
  });

  it("works out the year in Zambia, which starts two hours before UTC's", () => {
    expect(currentZambianYear(new Date("2026-12-31T22:30:00Z"))).toBe(2027);
    expect(currentZambianYear(new Date("2026-12-31T21:30:00Z"))).toBe(2026);
  });
});

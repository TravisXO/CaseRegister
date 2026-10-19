import { describe, expect, it } from "vitest";
import { currentZambianYear, formatCalendarDate, formatRelative, formatZambianTime } from "./time";

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

describe("relative time", () => {
  const now = Date.parse("2026-10-13T12:00:00Z");

  it.each([
    ["2026-10-13T11:59:50Z", "just now"],
    ["2026-10-13T11:55:00Z", "5 minutes ago"],
    ["2026-10-13T09:00:00Z", "3 hours ago"],
    ["2026-10-12T12:00:00Z", "yesterday"],
    ["2026-10-08T12:00:00Z", "5 days ago"],
    ["2026-10-13T13:00:00Z", "in 1 hour"],
    ["2026-10-16T12:00:00Z", "in 3 days"],
  ])("says %s as %s", (iso, expected) => {
    expect(formatRelative(iso, now)).toBe(expected);
  });

  it.each([null, undefined, "", "not a date"])("shows nothing for %s", (value) => {
    expect(formatRelative(value, now)).toBe("");
  });
});

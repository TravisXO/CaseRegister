import type { ReactNode } from "react";

/**
 * A table that may be wider than the screen, in a labelled region of its own that scrolls sideways, so at 320px wide the page itself never
 * does. The region can take keyboard focus (a scrolling area has to be reachable without a mouse, WCAG 2.1.1), and its label tells a screen
 * reader what is in it.
 */
export function TableRegion({ label, children }: { label: string; children: ReactNode }) {
  return (
    // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- a scrollable region must be focusable to be scrolled with the keyboard
    <div role="region" aria-label={label} tabIndex={0} className="table-wrap">
      {children}
    </div>
  );
}

/** On every page, including the one shown when something breaks: this is a demonstration, and nothing in it is real. */
export function DemoNotice() {
  return (
    <div role="note" className="border-b border-amber-300 bg-amber-100 px-4 py-2 text-center text-sm text-amber-950">
      <strong>Demo: all data is fake.</strong> Do not enter real personal details, or a password you use anywhere else.
    </div>
  );
}

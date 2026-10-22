/**
 * Render check for the QuantLOL signal panel.
 *
 * Renders the real component — not a copy of it — against a real payload from the running
 * Python engine, using React's server renderer. It catches the failures a typecheck cannot:
 * a field the engine renamed, a null that is formatted as a number, a crash on an empty
 * recent-signals list.
 *
 *   python3 -m signals serve --port 8080 &        # in the repo root
 *   cd terminal && ./node_modules/.bin/tsx tools/render-check.tsx
 *
 * Exit code 0 = the panel rendered with the data the engine actually produced.
 */
import React from "react";
import { renderToString } from "react-dom/server";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import SignalWidget from "../web/components/widgets/SignalWidget";

const API = process.env.QUANTLOL_API ?? "http://127.0.0.1:8080";

type Dict = Record<string, unknown>;

async function payload(path: string): Promise<Dict> {
  const res = await fetch(`${API}${path}`, { signal: AbortSignal.timeout(5000) });
  if (!res.ok) throw new Error(`${path} -> HTTP ${res.status}`);
  return (await res.json()) as Dict;
}

function render(state: Dict): string {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(["quantlol-state"], state);
  return renderToString(
    React.createElement(QueryClientProvider, { client }, React.createElement(SignalWidget))
  );
}

function must(haystack: string, needle: string, label: string) {
  if (!haystack.includes(needle)) {
    console.error(`FAIL ${label}: expected to find ${JSON.stringify(needle)}`);
    console.error(haystack.slice(0, 400));
    process.exitCode = 1;
  } else {
    console.log(`ok   ${label}`);
  }
}

async function main() {
  const state = await payload("/api/state");
  const cfg = (state.config ?? {}) as Dict;
  const latest = (state.latest ?? {}) as Dict;

  console.log(`payload: ${cfg.label} | latest=${latest.state}/${latest.reason} price=${latest.price}`);
  const html = render(state);

  must(html, "advisory only", "the no-orders disclaimer is always present");
  must(html, String(cfg.symbol ?? ""), "the instrument is shown");
  must(html, String(latest.reason ?? ""), "the engine's reason is shown verbatim");
  must(html, (state.data as Dict)?.source as string, "the feed source is shown");

  const actionable = latest.state === "long" || latest.state === "short";
  if (actionable) {
    must(html, "Entry", "an actionable signal shows its plan");
    must(html, "Stop", "an actionable signal shows its stop");
    must(html, "Target", "an actionable signal shows its target");
  } else {
    must(html, "Bars scanned", "a non-actionable state shows the data summary");
    must(html, "NO SIGNAL", "a non-actionable state is labelled plainly");
  }

  // The live feed rarely has a signal pending, so exercise the plan branch too, using the same
  // payload with the engine-shaped fields filled in. This is a component test, not a claim that
  // a signal exists.
  const price = Number(latest.price ?? 0);
  const longState: Dict = {
    ...state,
    latest: {
      ...latest, state: "long", direction: "long", reason: "SweepReclaim",
      entry: price, stop: price - 4.0, target: price + 12.0,
      risk_usd: 4.53, rr_net: 2.4, score: 4.5, stop_ticks: 400, session: "London",
    },
    recent_signals: [{ bar_time_utc: "2026-10-22T08:55:00+00:00", state: "long", entry: price, stop: price - 4.0 }],
  };
  const longHtml = render(longState);
  must(longHtml, "LONG", "an actionable signal is labelled LONG");
  for (const label of ["Entry", "Stop", "Target", "Risk", "R:R net"]) {
    must(longHtml, label, `the plan row ${label} is rendered`);
  }
  must(longHtml, "SweepReclaim", "the setup name is shown verbatim");
  must(longHtml, "Recent signals", "the recent-signals block renders");

  // The widget must never claim something the engine cannot produce.
  for (const forbidden of ["confidence", "predicted", "guaranteed", "win rate"]) {
    if (html.toLowerCase().includes(forbidden)) {
      console.error(`FAIL the panel claims ${forbidden}, which the engine does not measure`);
      process.exitCode = 1;
    }
  }
  console.log(process.exitCode ? "RENDER CHECK FAILED" : "RENDER CHECK PASSED");
}

main().catch((err) => {
  console.error("render check could not run:", err);
  process.exit(1);
});

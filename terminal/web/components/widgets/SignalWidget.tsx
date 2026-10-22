"use client";

import { useQuery } from "@tanstack/react-query";
import { apiGet, fmt } from "../../lib/api";

/**
 * QuantLOL Signals panel.
 *
 * Everything shown here is copied from the Python engine's /api/state payload — this component
 * decides nothing. In particular it never invents a "confidence", never shows a P&L the engine
 * did not measure, and always says "advisory only" because the engine cannot place an order.
 */

type Signal = {
  signal_id?: string | null;
  bar_time_utc?: string | null;
  state?: string;
  reason?: string;
  direction?: string | null;
  price?: number | null;
  entry?: number | null;
  stop?: number | null;
  target?: number | null;
  risk_usd?: number | null;
  rr_net?: number | null;
  score?: number | null;
  atr?: number | null;
  stop_ticks?: number | null;
  target_ticks?: number | null;
  engine_ms?: number | null;
  session?: string | null;
  spread?: number | null;
  bar_age_minutes?: number | null;
  evidence?: string[] | null;
};

type State = {
  app?: { version?: string; uptime_seconds?: number; engine?: { rules?: string } };
  config?: {
    label?: string; symbol?: string; timeframe_minutes?: number; data_source?: string;
    money_per_price?: number; max_risk_usd?: number; min_rr?: number; max_stop_distance?: number;
    volume?: string; key_present?: boolean; live_provider?: string;
  };
  data?: {
    ok?: boolean; source?: string; quality?: string; note?: string; bars?: number;
    last_bar_utc?: string | null; age_minutes?: number; fresh?: boolean; error?: string;
  };
  latest?: Signal | null;
  recent_signals?: Signal[];
  stats?: {
    evaluations?: number; signals?: number; errors?: number;
    by_reason?: Record<string, number>; last_eval_ms?: number | null; last_eval_utc?: string | null;
  };
  health?: { ok?: boolean; issues?: Array<{ severity: string; text: string }> };
};

const STATE_STYLE: Record<string, { label: string; cls: string }> = {
  long: { label: "LONG", cls: "text-[var(--up)] border-[var(--up)]" },
  short: { label: "SHORT", cls: "text-[var(--down)] border-[var(--down)]" },
  neutral: { label: "NO SIGNAL", cls: "text-[var(--text-dim)] border-[var(--border)]" },
  unavailable: { label: "NO DATA", cls: "text-[var(--down)] border-[var(--down)]" },
};

function Row({ label, value, cls = "" }: { label: string; value: string; cls?: string }) {
  return (
    <div className="flex justify-between border-b border-[#161616] py-0.5">
      <span className="dim">{label}</span>
      <span className={cls}>{value}</span>
    </div>
  );
}

function age(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined) return "—";
  if (seconds < 90) return `${Math.round(seconds)}s`;
  if (seconds < 5400) return `${Math.round(seconds / 60)}m`;
  return `${(seconds / 3600).toFixed(1)}h`;
}

export default function SignalWidget() {
  const { data, error, isLoading } = useQuery({
    queryKey: ["quantlol-state"],
    queryFn: () => apiGet<State>("/api/quantlol/state"),
    refetchInterval: 15_000,
    retry: false,
    // The engine itself polls every 60 s; the timestamp on screen is the engine's, not ours.
    staleTime: 5_000,
  });

  if (isLoading) return <div className="p-2 dim">Connecting to the signal engine…</div>;

  if (error) {
    return (
      <div className="p-2 text-[11px] leading-relaxed">
        <div className="down font-bold mb-1">ENGINE OFFLINE</div>
        <div className="dim mb-2">{(error as Error).message}</div>
        <pre className="border border-[var(--border)] p-1 text-[10px] overflow-x-auto">
python3 -m signals serve --port 8080
        </pre>
        <div className="dim mt-2">
          The panel is read-only: it shows the engine&apos;s decisions, it never computes its own.
        </div>
      </div>
    );
  }

  if (!data) return <div className="p-2 dim">No state returned by the engine.</div>;

  const latest = data.latest ?? {};
  const style = STATE_STYLE[latest.state ?? "neutral"] ?? STATE_STYLE.neutral;
  const data_ = data.data ?? {};
  const cfg = data.config ?? {};
  const stats = data.stats ?? {};
  const issues = data.health?.issues ?? [];
  const actionable = latest.state === "long" || latest.state === "short";

  return (
    <div className="p-2 text-[11px]">
      <div className="flex items-baseline gap-2 mb-1">
        <span className={`border px-1.5 py-0.5 text-[10px] font-bold ${style.cls}`}>{style.label}</span>
        <span className="dim">{cfg.timeframe_minutes ?? "?"}m</span>
        <span className="ml-auto dim text-[10px]">{cfg.symbol ?? ""}</span>
      </div>

      <div className="flex items-baseline gap-2 mb-1">
        <span className="text-xl font-bold">{fmt(latest.price)}</span>
        <span className="dim text-[10px] truncate">{latest.reason ?? ""}</span>
      </div>
      <div className="dim text-[10px] mb-2 truncate">{cfg.label ?? ""}</div>

      {actionable ? (
        <div className="grid grid-cols-2 gap-x-4 mb-2">
          <Row label="Entry" value={fmt(latest.entry)} />
          <Row label="Stop" value={fmt(latest.stop)} cls="down" />
          <Row label="Target" value={fmt(latest.target)} cls="up" />
          <Row label="Risk" value={latest.risk_usd !== null && latest.risk_usd !== undefined ? `$${fmt(latest.risk_usd)}` : "—"} />
          <Row label="R:R net" value={fmt(latest.rr_net)} />
          <Row label="Score" value={fmt(latest.score, 1)} />
          <Row label="Stop ticks" value={fmt(latest.stop_ticks, 0)} />
          <Row label="Session" value={latest.session ?? "—"} />
        </div>
      ) : (
        <div className="grid grid-cols-2 gap-x-4 mb-2">
          <Row label="Bars scanned" value={fmt(data_.bars, 0)} />
          <Row label="Evaluations" value={fmt(stats.evaluations, 0)} />
          <Row label="Signals" value={fmt(stats.signals, 0)} />
          <Row label="Engine" value={`${fmt(stats.last_eval_ms ?? latest.engine_ms)} ms`} />
        </div>
      )}

      <div className="dim text-[10px] mb-1">
        {data_.source ?? "?"} · {data_.quality ?? "?"} · {fmt(data_.bars, 0)} bars · bar age{" "}
        {fmt(data_.age_minutes, 0)}m{data_.fresh === false ? " (stale)" : ""}
      </div>

      {issues.length > 0 && (
        <div className="mb-1">
          {issues.slice(0, 3).map((i, n) => (
            <div
              key={n}
              className={`text-[10px] leading-snug ${i.severity === "error" ? "down" : i.severity === "warn" ? "text-[var(--amber)]" : "dim"}`}
            >
              [{i.severity}] {i.text}
            </div>
          ))}
        </div>
      )}

      {(data.recent_signals ?? []).length > 0 && (
        <div className="mt-1 border-t border-[var(--border)] pt-1">
          <div className="dim text-[10px] uppercase tracking-wider mb-0.5">Recent signals</div>
          {(data.recent_signals ?? []).slice(0, 5).map((s, n) => (
            <div key={n} className="flex justify-between text-[10px]">
              <span className="dim">{(s.bar_time_utc ?? "").replace("T", " ").slice(5, 16)}</span>
              <span className={(s.state ?? "") === "long" ? "up" : "down"}>{(s.state ?? "").toUpperCase()}</span>
              <span>{fmt(s.entry)}</span>
              <span className="dim">stop {fmt(s.stop)}</span>
            </div>
          ))}
        </div>
      )}

      <div className="mt-1 pt-1 border-t border-[var(--border)] flex justify-between text-[9px] dim">
        <span>advisory only · no orders</span>
        <span>uptime {age(data.app?.uptime_seconds)}</span>
      </div>
    </div>
  );
}

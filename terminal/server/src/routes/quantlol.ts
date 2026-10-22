import { Router } from "express";

/**
 * QuantLOL Signals bridge.
 *
 * The signal engine is the Python package in `<repo>/signals` — a stdlib-only, read-only
 * service (`python3 -m signals serve --port 8080`). It owns the rules, the risk arithmetic and
 * the signal log; this router does nothing but forward read-only GETs to it.
 *
 * There is deliberately no route here that can place, modify or close anything: the engine has
 * no broker connection at all, and this bridge would have nowhere to send such a request.
 *
 * Point it somewhere else with QUANTLOL_URL (e.g. a second engine running BTC on port 8081).
 */
const BASE = (process.env.QUANTLOL_URL ?? "http://127.0.0.1:8080").replace(/\/+$/, "");
const TIMEOUT_MS = Number(process.env.QUANTLOL_TIMEOUT_MS ?? 4000);

export const quantlolRouter = Router();

type EnginePayload = Record<string, unknown>;

async function callEngine(path: string): Promise<EnginePayload> {
  const res = await fetch(`${BASE}${path}`, {
    signal: AbortSignal.timeout(TIMEOUT_MS),
    cache: "no-store",
    headers: { accept: "application/json" },
  });
  const text = await res.text();
  let body: EnginePayload;
  try {
    body = text ? (JSON.parse(text) as EnginePayload) : {};
  } catch {
    throw new Error(`engine returned non-JSON (HTTP ${res.status})`);
  }
  if (!res.ok) throw new Error(String(body.error ?? `HTTP ${res.status}`));
  return body;
}

const OFFLINE = {
  error: "QuantLOL engine offline",
  hint: "start it with:  python3 -m signals serve --port 8080   (or set QUANTLOL_URL)",
};

function unreachable(res: import("express").Response, err: unknown) {
  const detail = err instanceof Error ? err.message : String(err);
  res.status(503).json({ ...OFFLINE, url: BASE, detail });
}

quantlolRouter.get("/status", async (_req, res) => {
  const started = Date.now();
  try {
    const state = await callEngine("/api/state");
    res.json({
      ok: true,
      url: BASE,
      latencyMs: Date.now() - started,
      engine: (state as { app?: unknown }).app ?? null,
      config: (state as { config?: unknown }).config ?? null,
      latest: (state as { latest?: unknown }).latest ?? null,
      health: (state as { health?: { ok?: boolean } }).health?.ok ?? null,
    });
  } catch (err) {
    const detail = err instanceof Error ? err.message : String(err);
    res.status(503).json({ ok: false, ...OFFLINE, url: BASE, detail, latencyMs: Date.now() - started });
  }
});

quantlolRouter.get("/state", async (_req, res) => {
  try {
    res.json(await callEngine("/api/state"));
  } catch (err) {
    unreachable(res, err);
  }
});

quantlolRouter.get("/signals", async (_req, res) => {
  try {
    const state = await callEngine("/api/state");
    res.json({
      config: state.config ?? null,
      recent_signals: state.recent_signals ?? [],
      stats: state.stats ?? null,
    });
  } catch (err) {
    unreachable(res, err);
  }
});

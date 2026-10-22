# Vendored copy — provenance and changes

`terminal/` is a **vendored copy of [ErTasselli/OpenTerminal](https://github.com/ErTasselli/OpenTerminal)**
(MIT, see `terminal/LICENSE`), pinned at commit:

```
aed097c680cd8ec1c391ae06966babe7d6d91fc6   2026-10-06
```

It is here because QuantLOL's signal engine had no terminal to live in, and OpenTerminal is a
solid, keyless, MIT-licensed one. Upstream is not a dependency we track automatically: the copy
is pinned, and upgrading means re-copying and re-applying the changes below.

## What we changed

| file | change |
|---|---|
| `server/src/routes/quantlol.ts` | **new** — read-only bridge `GET /api/quantlol/{status,state,signals}` to the Python engine (`QUANTLOL_URL`, default `http://127.0.0.1:8080`) |
| `server/src/index.ts` | mounts `/api/quantlol` before the market router |
| `web/components/widgets/SignalWidget.tsx` | **new** — the QuantLOL signal panel |
| `web/components/Workspace.tsx` | renders the new widget type |
| `web/components/Sidebar.tsx` | adds a `SIGNALS` entry to the add-widget list |
| `web/store/terminal.ts` | new `signal` widget type, size, and a slot in the default workspace |
| `web/next.config.ts` | `allowedDevOrigins` (Next 15 blocks cross-origin dev/HMR requests through a proxy host) |
| `package.json` | `npm run check:signals` — renders the panel against the live engine |
| `tools/render-check.tsx`, `tools/tsconfig.render.json` | **new** — the render check behind that script |
| `README.md` | a short QuantLOL section at the top |

Nothing else was modified. The upstream provider code, caching, rate limiting, auth and every
other widget are untouched — including their fallback chains.

## What was removed

* `docs/screenshots/` (3.2 MB of PNGs) — they only decorate the upstream README. The README's
  image links therefore do not resolve in this copy; see the upstream repo for them.
* `.claude/` (local tool configuration, not part of the product).

## Licence

MIT. The original copyright notice is preserved in `terminal/LICENSE`. Our added files are part
of QuantLOL and carry the same MIT terms.

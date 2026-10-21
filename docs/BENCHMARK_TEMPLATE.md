# Benchmark report template

Copy this file, fill in every `___`, and delete nothing. A number without its conditions is not a
benchmark; it is a rumour.

**Rules**

- Benchmark **before** changing architecture, and benchmark the same workload again afterwards.
- Identical data, identical configuration, identical machine/VM class. If any of those changed, it is
  a different experiment.
- Execution performance is not profitability. Never present a latency improvement as a return.
- Report sample counts. A p99 from 40 ticks is not a p99.
- Internal latency (your code) and network/RTT (broker) are separate rows. Do not blend them.

---

## 1. Environment

| Item | Value |
|---|---|
| Machine / VM | ___ |
| CPU / cores | ___ |
| RAM | ___ |
| OS | ___ |
| .NET runtime | ___ (`dotnet --info`) |
| cTrader build | ___ |
| Broker / server | ___ |
| Account type | Demo / Live |
| Symbol | XAUUSD (contract size ___, tick size ___, tick value ___) |
| Data set | instrument, period, bars: ___ |
| Bot version / commit | ___ |
| Configuration hash | ___ (list any non-default parameter below) |

Non-default parameters: ___

## 2. Per-stage latency (one row per stage, from the bot's own instrumentation)

Stages: `tick->signal_eval`, `signal->risk_approval`, `risk->order_submit`, `submit->broker_ack`,
`submit->confirmed_fill`, `decision->fill_total`, `slow_path_total`, `on_tick_total`,
`external_data_refresh`.

| Stage | n | median (ms) | p95 (ms) | p99 (ms) | max (ms) | clock |
|---|---|---|---|---|---|---|
| tick->signal_eval | ___ | ___ | ___ | ___ | ___ | Stopwatch (monotonic) |
| signal->risk_approval | ___ | ___ | ___ | ___ | ___ | |
| risk->order_submit | ___ | ___ | ___ | ___ | ___ | |
| submit->broker_ack | ___ | ___ | ___ | ___ | ___ | network included |
| submit->confirmed_fill | ___ | ___ | ___ | ___ | ___ | network included |
| decision->fill_total | ___ | ___ | ___ | ___ | ___ | end to end |
| slow_path_total | ___ | ___ | ___ | ___ | ___ | M5 bar close |
| on_tick_total | ___ | ___ | ___ | ___ | ___ | hot path |
| external_data_refresh | ___ | ___ | ___ | ___ | ___ | async |

Notes on what is internal vs network: `risk->order_submit` ends when the request is handed to the
API; everything after that depends on the broker and the connection. Report them separately.

## 3. Allocations / GC (only if measured)

Measure with `GC.GetAllocatedBytesForCurrentThread()` around the stage, or with a profiler. If you
did not measure it, write "not measured" — do not estimate.

| Path | Allocated bytes / call | Gen0 collections / 1k calls | Method |
|---|---|---|---|
| OnTick (no new closed bar) | ___ | ___ | ___ |
| Slow path (bar close) | ___ | ___ | ___ |
| Journal append (buffered) | ___ | ___ | ___ |

## 4. External-data overhead

| Item | Value |
|---|---|
| GEX/VIX refresh path | background task; never on the tick thread |
| Time held on tick thread per check | ___ µs |
| Refresh duration (file / HTTP) | ___ ms / ___ ms |
| Worst observed refresh | ___ ms |
| Did any tick block on the network? | yes / no (expected: no) |

## 5. Order timing

| Item | Value |
|---|---|
| Submit -> broker ack (p50 / p95 / max) | ___ / ___ / ___ ms |
| Submit -> confirmed fill (p50 / p95 / max) | ___ / ___ / ___ ms |
| Rejects observed | ___ (codes: ___) |
| Partial fills observed | ___ |
| Orders cancelled / timed out | ___ |

## 6. Before/after comparison (only when an architecture change is made)

| Metric | Before | After | Workload identical? |
|---|---|---|---|
| on_tick_total p99 | ___ | ___ | yes / no |
| slow_path_total p99 | ___ | ___ | yes / no |
| allocations / tick | ___ | ___ | yes / no |
| decision->fill p95 | ___ | ___ | yes / no |

State the measured bottleneck that motivated the change, and whether the change moved it. If the
answer is "no measurable difference", say that.

## 7. Conclusions

- Biggest measured bottleneck: ___
- Evidence: ___
- What was **not** measured and is therefore unknown: ___

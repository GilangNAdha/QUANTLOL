"""QuantLOL Signals - a standalone, broker-free signal application.

It reuses the exact rule set of the cBot / backtest (research/quantlol_ref.py, which the C#
core is pinned to by tests/QuantLOL.Tests/ParityTests.cs) but never talks to a broker:

    data in  ->  structure / AMT / order-flow / VIX / GEX  ->  signal or explicit rejection
             ->  JSON + CSV + local dashboard

No cTrader, no cAlgo, no orders, no API keys. See docs/SIGNAL_APP.md.
"""

__version__ = "1.0.0"

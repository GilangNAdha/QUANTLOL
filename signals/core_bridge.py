"""
Loads the shared rule engine.

There is exactly ONE implementation of the rules in this repository (the Python reference),
and the C# cBot core is pinned to it by cross-language parity tests. The signal application
imports that same module instead of copying logic, so a rule can never drift between the
backtest, the bot and this app.
"""
import os
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_RESEARCH = os.path.join(REPO_ROOT, "research")
if _RESEARCH not in sys.path:
    sys.path.insert(0, _RESEARCH)

import quantlol_ref as core  # noqa: E402  (import after the explicit path edit)


def describe_engine():
    return {
        "module": os.path.relpath(core.__file__, REPO_ROOT),
        "rules": "swing/BOS/CHoCH, sweeps, FVG/OB, session profile, CLV order-flow proxy, "
                 "USD risk cap, drawdown guard",
        "shared_with": ["cBot C# core (parity tests)", "research backtest"],
    }

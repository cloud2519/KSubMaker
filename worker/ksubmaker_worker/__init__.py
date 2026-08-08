"""KSubMaker Python AI worker.

The worker is a long-lived child process of the C# host. It speaks a JSON-lines protocol on
stdio: one compact JSON object per line on stdout, nothing else. Everything else -- logs,
library chatter, tracebacks -- goes to stderr.

Nothing in this package imports torch / ctranslate2 / faster-whisper at module level: those
imports live inside the functions that need them so the package (and its test suite) loads on a
machine with no models and no GPU.
"""

from __future__ import annotations

__all__ = ["__version__", "PROTOCOL_VERSION"]

__version__ = "1.0.0"

# Re-exported for convenience; the authoritative definition lives in protocol.py, which mirrors
# src/KSubMaker.WorkerProtocol/ProtocolConstants.cs.
PROTOCOL_VERSION = "1.0"
